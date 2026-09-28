using Qdrant.Client.Grpc;

namespace Iverson.Vector;

/// <summary>One CHUNKS row (spec §1): the three chunk columns, plus the chunk's own vector when requested and
/// present (null otherwise — SIMILARITY then reads NULL).</summary>
public sealed record ChunkRow(string ParentKey, int ChunkIndex, string Text, float[]? Vector);

/// <param name="ChunksCollection">The tenant's resolved chunks collection.</param>
/// <param name="Filter">The caller's chunk filter (<c>BuildChunksFilter</c> + <c>ApplyOwnership</c>), or null.</param>
/// <param name="Field">The chunk field's canonical property name, matched against the payload <c>field</c>.</param>
/// <param name="VectorName">The chunk vector to return, or null when the request uses no SIMILARITY.</param>
/// <param name="BatchRows">Phase-2 batch bound, in chunks (spec §4).</param>
/// <param name="MaxPartitionRows">Per-parent bound, checked on phase 1's counts before any phase-2 read; a parent with
/// more chunks than this throws <c>PatternBudgetExceededException</c> (spec §4).</param>
/// <param name="MaxRowsScanned">Phase-1 bound; more chunks than this throws <c>PatternBudgetExceededException</c>.</param>
public sealed record ChunkRowQuery(
    string ChunksCollection, Filter? Filter, string Field, string? VectorName, int BatchRows, int MaxPartitionRows,
    int MaxRowsScanned);

/// <summary>The CHUNKS source for MatchPattern (spec §3.2).</summary>
public interface IChunkRowSource
{
    /// <summary>Streams the chunks ordered by parent key (ordinal), then chunk index (numeric). A missing
    /// collection yields nothing.</summary>
    IAsyncEnumerable<ChunkRow> ReadAsync(ChunkRowQuery query, CancellationToken ct = default);
}
