using System.Globalization;
using System.Runtime.CompilerServices;
using Grpc.Core;
using Iverson.Patterns;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace Iverson.Vector;

/// <summary>The default <see cref="IChunkRowSource"/>: a two-phase Qdrant scroll (spec §3.2, §4).
/// Phase 1 counts chunks per parent to enforce <see cref="ChunkRowQuery.MaxRowsScanned"/> and
/// <see cref="ChunkRowQuery.MaxPartitionRows"/> (before any phase-2 read, so an oversized parent is never loaded) and
/// to build <see cref="ChunkRowQuery.BatchRows"/>-bounded batches of whole parents; phase 2 re-scrolls each batch for its
/// chunk text (and vector, when requested), grouped and ordered for output.</summary>
public sealed class QdrantChunkRowSource(IVectorQueryService vector, IntelligenceTenantScope tenantScope) : IChunkRowSource
{
    private const uint ParentPageSize = 1024u;

    // 256 × (768 floats + a chunk's text) stays well under Grpc.Net.Client's 4 MB message cap —
    // the same reasoning as RetrieveNamedVectorAsync's 512-vector batch.
    private const uint ChunkPageSize = 256u;

    public async IAsyncEnumerable<ChunkRow> ReadAsync(ChunkRowQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var baseFilter = query.Filter?.Clone() ?? new Filter();
        baseFilter.Must.Add(Conditions.MatchKeyword("field", query.Field));

        var (parentCounts, collectionExists) = await ScanParentsAsync(query, baseFilter, ct);
        if (!collectionExists) yield break;

        // Spec §4 bounds memory at MaxPartitionRows: a parent over it must fail here, before phase 2 reads its chunks
        // (text and vectors) in full — the batcher's own check would only fire once they were all in memory.
        if (parentCounts.Values.Any(count => count > query.MaxPartitionRows))
            throw new PatternBudgetExceededException("MaxPartitionRows");

        var sortedParents = parentCounts.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        var batches = BuildBatches(sortedParents, parentCounts, query.BatchRows);

        var vectorMissing = false;
        foreach (var batch in batches)
        {
            var batchFilter = baseFilter.Clone();
            batchFilter.Must.Add(Conditions.Match("parent_id", batch));

            var points = vectorMissing
                ? await ScrollBatchAsync(query.ChunksCollection, batchFilter, null, ct)
                : await ScrollBatchWithFallbackAsync(query.ChunksCollection, batchFilter, query.VectorName, ct,
                    onVectorMissing: () => vectorMissing = true);

            foreach (var group in points.GroupBy(p => p.Payload["parent_id"]).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                foreach (var point in group.OrderBy(p => int.Parse(p.Payload["chunk_index"], CultureInfo.InvariantCulture)))
                {
                    ct.ThrowIfCancellationRequested();
                    yield return new ChunkRow(
                        group.Key,
                        int.Parse(point.Payload["chunk_index"], CultureInfo.InvariantCulture),
                        point.Payload["text"],
                        point.Vector);
                }
            }
        }
    }

    /// <summary>Phase 1: pages the base filter with only the <c>parent_id</c> payload, counting chunks per parent
    /// and enforcing <see cref="ChunkRowQuery.MaxRowsScanned"/>. Returns <c>(counts, false)</c> when the collection
    /// does not exist (NotFound on the first page).</summary>
    private async Task<(Dictionary<string, int> Counts, bool CollectionExists)> ScanParentsAsync(
        ChunkRowQuery query, Filter baseFilter, CancellationToken ct)
    {
        var counts = new Dictionary<string, int>();
        var total = 0;
        PointId? offset = null;
        var first = true;

        while (true)
        {
            VectorScrollPage? page = null;
            var notFound = false;
            try
            {
                page = await PageAsync(query.ChunksCollection, baseFilter, ["parent_id"], null, ParentPageSize, offset, ct);
            }
            catch (RpcException e) when (first && e.StatusCode == StatusCode.NotFound)
            {
                notFound = true;
            }

            if (notFound) return (counts, false);
            first = false;

            foreach (var point in page!.Points)
            {
                var parentId = point.Payload["parent_id"];
                counts[parentId] = counts.GetValueOrDefault(parentId) + 1;
                total++;
                if (total > query.MaxRowsScanned)
                    throw new PatternBudgetExceededException("MaxRowsScanned");
            }

            offset = page.NextOffset;
            if (offset is null) return (counts, true);
        }
    }

    /// <summary>Walks the ordinally-sorted parents, closing the current batch whenever adding the next parent
    /// would exceed <paramref name="batchRows"/> and the batch is non-empty — so a parent larger than
    /// <paramref name="batchRows"/> forms a batch on its own.</summary>
    private static List<List<string>> BuildBatches(
        IReadOnlyList<string> sortedParents, IReadOnlyDictionary<string, int> counts, int batchRows)
    {
        var batches = new List<List<string>>();
        var current = new List<string>();
        var currentCount = 0;

        foreach (var parent in sortedParents)
        {
            var count = counts[parent];
            if (current.Count > 0 && currentCount + count > batchRows)
            {
                batches.Add(current);
                current = [];
                currentCount = 0;
            }

            current.Add(parent);
            currentCount += count;
        }

        if (current.Count > 0) batches.Add(current);
        return batches;
    }

    /// <summary>Pages a phase-2 batch with the given vector name (or none). Used once vector fallback has
    /// already triggered for a prior batch, so this batch never asks for a vector at all.</summary>
    private async Task<List<ScrolledPoint>> ScrollBatchAsync(
        string collection, Filter batchFilter, string? vectorName, CancellationToken ct)
    {
        var points = new List<ScrolledPoint>();
        PointId? offset = null;
        while (true)
        {
            var page = await PageAsync(collection, batchFilter, ["text", "parent_id", "chunk_index"], vectorName, ChunkPageSize, offset, ct);
            points.AddRange(page.Points);
            offset = page.NextOffset;
            if (offset is null) return points;
        }
    }

    /// <summary>Pages a phase-2 batch with the requested vector, restarting from the top without it if Qdrant
    /// reports the vector doesn't exist on this collection (spec §3.3) — every later row's vector is then null.</summary>
    private async Task<List<ScrolledPoint>> ScrollBatchWithFallbackAsync(
        string collection, Filter batchFilter, string? vectorName, CancellationToken ct, Action onVectorMissing)
    {
        if (vectorName is null) return await ScrollBatchAsync(collection, batchFilter, null, ct);

        var points = new List<ScrolledPoint>();
        PointId? offset = null;
        while (true)
        {
            VectorScrollPage page;
            try
            {
                page = await PageAsync(collection, batchFilter, ["text", "parent_id", "chunk_index"], vectorName, ChunkPageSize, offset, ct);
            }
            catch (RpcException e) when (e.StatusCode == StatusCode.InvalidArgument
                                          && e.Status.Detail.Contains("Not existing vector name"))
            {
                onVectorMissing();
                return await ScrollBatchAsync(collection, batchFilter, null, ct);
            }

            points.AddRange(page.Points);
            offset = page.NextOffset;
            if (offset is null) return points;
        }
    }

    /// <summary>One Qdrant call per scope — a scope never spans a <c>yield</c>.</summary>
    private async Task<VectorScrollPage> PageAsync(string collection, Filter filter, string[] payload, string? vectorName,
        uint pageSize, PointId? offset, CancellationToken ct)
    {
        using (RequestHeaders.Use("api-key", tenantScope.MintScopedApiKey(collection, readOnly: true)))
            return await vector.ScrollAsync(collection, filter, payload, vectorName, pageSize, offset, ct);
    }
}
