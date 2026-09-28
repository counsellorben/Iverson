using FluentAssertions;
using Iverson.Patterns;
using NSubstitute;
using Qdrant.Client.Grpc;
using Xunit;

namespace Iverson.Vector.Tests;

[Trait("Category", "Integration")]
[Collection(ContainerCollection.Name)]
public sealed class ChunkRowSourceIntegrationTests(QdrantContainerFixture fixture) : IClassFixture<QdrantContainerFixture>
{
    private readonly IntelligenceVectorService _svc = fixture.Service;
    private readonly IntelligenceCollectionManager _mgr = fixture.CollectionManager;
    private readonly IntelligenceTenantScope _scope = new("test-signing-key-0123456789abcdef");
    private ulong _nextId = 1;

    private async Task<string> CollectionAsync(string vectorName = "body_vector")
    {
        var name = "ck_" + Guid.NewGuid().ToString("N")[..8];
        await _mgr.ApplyCollectionAsync(new CollectionSchema(name, [new NamedVector(vectorName, 4)], []));
        return name;
    }

    private Task AddChunkAsync(string collection, string parent, int index, string field = "Body",
        string owner = "u1", string vectorName = "body_vector", float first = 1f) =>
        _svc.UpsertNamedAsync(collection, _nextId++,
            new Dictionary<string, float[]> { [vectorName] = [first, 0f, 0f, index] },
            new Dictionary<string, object>
            {
                ["text"] = $"{parent}#{index}", ["parent_id"] = parent, ["field"] = field,
                ["chunk_index"] = index.ToString(), ["ownerId"] = owner,
            });

    /// <summary>Bulk-seeds many chunks in a handful of Qdrant calls (500 points/call) rather than one round
    /// trip per point — used only by the page-boundary tests below, which need hundreds to over a thousand
    /// points and would otherwise dominate the run time.</summary>
    private async Task SeedBulkAsync(string collection, IEnumerable<(string Parent, int Index)> chunks,
        string vectorName = "body_vector", string field = "Body", string owner = "u1")
    {
        var points = new List<PointStruct>();
        foreach (var (parent, index) in chunks)
        {
            var named = new NamedVectors();
            named.Vectors[vectorName] = new float[] { 1f, 0f, 0f, index };
            var point = new PointStruct { Id = _nextId++, Vectors = new Vectors { Vectors_ = named } };
            point.Payload["text"] = IntelligenceVectorService.ToQdrantValue($"{parent}#{index}");
            point.Payload["parent_id"] = IntelligenceVectorService.ToQdrantValue(parent);
            point.Payload["field"] = IntelligenceVectorService.ToQdrantValue(field);
            point.Payload["chunk_index"] = IntelligenceVectorService.ToQdrantValue(index.ToString());
            point.Payload["ownerId"] = IntelligenceVectorService.ToQdrantValue(owner);
            points.Add(point);
        }

        foreach (var batch in points.Chunk(500))
            await fixture.Client.UpsertAsync(collection, batch);
    }

    private static async Task<List<ChunkRow>> ReadAllAsync(IChunkRowSource source, ChunkRowQuery query)
    {
        var rows = new List<ChunkRow>();
        await foreach (var row in source.ReadAsync(query)) rows.Add(row);
        return rows;
    }

    private QdrantChunkRowSource Source(IVectorQueryService? vector = null) => new(vector ?? _svc, _scope);

    private static ChunkRowQuery Query(string collection, string? vectorName = null, Filter? filter = null,
        int batchRows = 2_000, int maxPartitionRows = 100_000, int maxRowsScanned = 100_000) =>
        new(collection, filter, "Body", vectorName, batchRows, maxPartitionRows, maxRowsScanned);

    [Fact]
    public async Task Chunks_arrive_grouped_by_parent_in_ordinal_order_and_by_numeric_chunk_index()
    {
        var col = await CollectionAsync();
        foreach (var (parent, index) in new[] { ("b", 0), ("a", 10), ("B", 0), ("a", 2), ("a", 0), ("a", 1) })
            await AddChunkAsync(col, parent, index);

        var rows = await ReadAllAsync(Source(), Query(col));

        rows.Select(r => (r.ParentKey, r.ChunkIndex)).Should().Equal(
            ("B", 0), ("a", 0), ("a", 1), ("a", 2), ("a", 10), ("b", 0));
        rows[1].Text.Should().Be("a#0");
    }

    [Fact]
    public async Task Only_the_requested_field_and_the_callers_filter_are_read()
    {
        var col = await CollectionAsync();
        await AddChunkAsync(col, "a", 0);
        await AddChunkAsync(col, "a", 1, field: "Title");
        await AddChunkAsync(col, "b", 0, owner: "u2");

        var ownership = IntelligenceFilterBuilder.ApplyOwnership(null, ownershipRequired: true, "ownerId", "u1");
        var rows = await ReadAllAsync(Source(), Query(col, filter: ownership));

        rows.Select(r => (r.ParentKey, r.ChunkIndex)).Should().Equal(("a", 0));
    }

    [Fact]
    public async Task Vectors_come_back_only_when_requested()
    {
        var col = await CollectionAsync();
        await AddChunkAsync(col, "a", 3, first: 0.5f);

        (await ReadAllAsync(Source(), Query(col))).Single().Vector.Should().BeNull();
        // Qdrant's Cosine collections store the vector normalised to unit length.
        (await ReadAllAsync(Source(), Query(col, vectorName: "body_vector"))).Single().Vector
            .Should().Equal(new[] { 0.5f, 0f, 0f, 3f }.Select(x => x / MathF.Sqrt(9.25f)), (a, e) => Math.Abs(a - e) < 1e-6f);
    }

    [Fact]
    public async Task A_missing_collection_yields_no_rows()
    {
        var rows = await ReadAllAsync(Source(), Query("ck_missing_" + Guid.NewGuid().ToString("N")[..8], vectorName: "body_vector"));

        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task A_collection_without_the_vector_re_issues_the_scroll_without_it()
    {
        var col = await CollectionAsync(vectorName: "other_vector");
        await AddChunkAsync(col, "a", 0, vectorName: "other_vector");

        var rows = await ReadAllAsync(Source(), Query(col, vectorName: "body_vector"));

        rows.Should().ContainSingle().Which.Vector.Should().BeNull();
    }

    [Fact]
    public async Task Phase_one_over_MaxRowsScanned_raises_the_budget_exception()
    {
        var col = await CollectionAsync();
        for (var i = 0; i < 5; i++) await AddChunkAsync(col, "a", i);

        var act = () => ReadAllAsync(Source(), Query(col, maxRowsScanned: 4));

        (await act.Should().ThrowAsync<PatternBudgetExceededException>()).Which.BudgetName.Should().Be("MaxRowsScanned");
        (await ReadAllAsync(Source(), Query(col, maxRowsScanned: 5))).Should().HaveCount(5);
    }

    [Fact]
    public async Task A_parent_over_MaxPartitionRows_raises_the_budget_exception_before_any_phase_two_read()
    {
        // "a" (1 chunk) sorts before "b" (4 chunks) and is its own batch: were the check made only as phase 2 reached
        // "b", "a" would already have been read and yielded.
        var col = await CollectionAsync();
        await AddChunkAsync(col, "a", 0);
        for (var i = 0; i < 4; i++) await AddChunkAsync(col, "b", i);

        var phaseTwo = 0;
        var spy = Substitute.For<IVectorQueryService>();
        spy.ScrollAsync(default!, default, default!, default, default, default, default).ReturnsForAnyArgs(ci =>
        {
            if (ci.ArgAt<IReadOnlyList<string>>(2).Contains("text")) phaseTwo++;
            return _svc.ScrollAsync(ci.ArgAt<string>(0), ci.ArgAt<Filter?>(1), ci.ArgAt<IReadOnlyList<string>>(2),
                ci.ArgAt<string?>(3), ci.ArgAt<uint>(4), ci.ArgAt<PointId?>(5), ci.ArgAt<CancellationToken>(6));
        });
        var yielded = new List<ChunkRow>();

        var act = async () =>
        {
            await foreach (var row in Source(spy).ReadAsync(Query(col, vectorName: "body_vector", batchRows: 1, maxPartitionRows: 3)))
                yielded.Add(row);
        };

        (await act.Should().ThrowAsync<PatternBudgetExceededException>()).Which.BudgetName.Should().Be("MaxPartitionRows");
        phaseTwo.Should().Be(0, "the check runs on phase 1's counts, before any phase-2 read");
        yielded.Should().BeEmpty();
        (await ReadAllAsync(Source(), Query(col, maxPartitionRows: 4))).Should().HaveCount(5);
    }

    [Theory]
    [InlineData(3, 2)]   // [a(2)] then [b(2) c(1)]
    [InlineData(2, 3)]   // [a] [b] [c]
    [InlineData(1, 3)]   // a parent larger than BatchRows forms a batch on its own
    public async Task Phase_two_batches_whole_parents_by_BatchRows(int batchRows, int expectedPhaseTwoScrolls)
    {
        var col = await CollectionAsync();
        foreach (var (parent, index) in new[] { ("a", 0), ("a", 1), ("b", 0), ("b", 1), ("c", 0) })
            await AddChunkAsync(col, parent, index);

        var phaseTwo = 0;
        var spy = Substitute.For<IVectorQueryService>();
        spy.ScrollAsync(default!, default, default!, default, default, default, default).ReturnsForAnyArgs(ci =>
        {
            if (ci.ArgAt<IReadOnlyList<string>>(2).Contains("text")) phaseTwo++;
            return _svc.ScrollAsync(ci.ArgAt<string>(0), ci.ArgAt<Filter?>(1), ci.ArgAt<IReadOnlyList<string>>(2),
                ci.ArgAt<string?>(3), ci.ArgAt<uint>(4), ci.ArgAt<PointId?>(5), ci.ArgAt<CancellationToken>(6));
        });

        var rows = await ReadAllAsync(Source(spy), Query(col, batchRows: batchRows));

        rows.Should().HaveCount(5);
        phaseTwo.Should().Be(expectedPhaseTwoScrolls);
    }

    // ── Page-boundary coverage (fix round 1) ───────────────────────────────
    //
    // Every test above stays inside one page of ParentPageSize (1024) or ChunkPageSize (256), so none
    // of them can tell a correct `offset = page.NextOffset` continuation from one that always stops
    // after the first page. These three seed past each loop's page size and check every row still
    // arrives, in order.

    [Fact]
    public async Task Phase_one_scan_pages_past_ParentPageSize_and_returns_every_parent_in_order()
    {
        var col = await CollectionAsync();
        const int parentCount = 1_100; // > ParentPageSize (1024)
        var parents = Enumerable.Range(0, parentCount).Select(i => $"p{i:D4}").ToList();
        await SeedBulkAsync(col, parents.Select(p => (p, 0)));

        var rows = await ReadAllAsync(Source(), Query(col));

        rows.Select(r => r.ParentKey).Should().Equal(parents.OrderBy(p => p, StringComparer.Ordinal));
        rows.Should().OnlyContain(r => r.ChunkIndex == 0);
    }

    [Fact]
    public async Task Phase_two_normal_scroll_pages_past_ChunkPageSize_within_one_batch()
    {
        var col = await CollectionAsync();
        const int chunkCount = 300; // > ChunkPageSize (256)
        await SeedBulkAsync(col, Enumerable.Range(0, chunkCount).Select(i => ("a", i)));

        var rows = await ReadAllAsync(Source(), Query(col, vectorName: "body_vector"));

        rows.Select(r => r.ChunkIndex).Should().Equal(Enumerable.Range(0, chunkCount));
        rows.Should().OnlyContain(r => r.Vector != null);
    }

    [Fact]
    public async Task Phase_two_vector_fallback_scroll_pages_past_ChunkPageSize_within_one_batch()
    {
        var col = await CollectionAsync(vectorName: "other_vector");
        const int chunkCount = 300; // > ChunkPageSize (256)
        await SeedBulkAsync(col, Enumerable.Range(0, chunkCount).Select(i => ("a", i)), vectorName: "other_vector");

        var rows = await ReadAllAsync(Source(), Query(col, vectorName: "body_vector"));

        rows.Select(r => r.ChunkIndex).Should().Equal(Enumerable.Range(0, chunkCount));
        rows.Should().OnlyContain(r => r.Vector == null);
    }
}
