namespace Iverson.LoadTest.Benchmark;

/// <summary>
/// The outcome of aggregating one query's chunk results: the document-level ranking, plus any
/// parent keys the key map could not resolve.
///
/// <para>Both are returned together deliberately. An unresolved parent is a document that was in the
/// index but not in this run's key map, so the ranking is built from a corpus the caller cannot fully
/// name — it must not be scored. Returning the ranking alone would let that fact be dropped silently,
/// which is the failure this type exists to make visible.</para>
/// </summary>
public sealed record ChunkAggregation(
    IReadOnlyList<(string DocId, double Score)> Ranked,
    IReadOnlyList<string>                       UnresolvedParentKeys);

/// <summary>
/// <see cref="ChunkAggregation"/> plus each document's winning (max-passage) chunk text — the
/// input the cross-encoder scores in Phase 1 (spec §3.3).
/// </summary>
public sealed record WinningChunkAggregation(
    IReadOnlyList<(string DocId, double Score, string Text)> Ranked,
    IReadOnlyList<string>                                    UnresolvedParentKeys);

/// <summary>
/// Collapses a stream of chunk-level search results down to one row per parent document
/// (max-passage aggregation): the parent's score is the maximum score among its chunks, not
/// the first chunk seen or the sum of its chunks. <c>SearchChunksRequest.top_k</c> counts
/// chunks and the server does not dedup by parent (spec A22), so this is what turns a
/// chunk-budget-sized result set back into a document-ranked list comparable to SearchSimilar's.
/// </summary>
public static class MaxPassageAggregator
{
    /// <summary>
    /// Aggregates one query's chunks. Parent keys absent from <paramref name="keyMap"/> are excluded
    /// from the ranking and returned in <see cref="ChunkAggregation.UnresolvedParentKeys"/> rather
    /// than throwing, so the caller can survey the full extent of the mismatch across every query and
    /// report it once. Throwing on the first one told the operator that a mismatch existed but not
    /// how large it was, and killed the run before any diagnostic output was written.
    /// </summary>
    public static ChunkAggregation Aggregate(
        IEnumerable<(string ParentKey, double Score)> chunks,
        IReadOnlyDictionary<string, string> keyMap,
        int limit) =>
        Aggregate(chunks, keyMap, limit, beta: 0);

    /// <summary>
    /// Same aggregation, scoring each document via <see cref="DocumentRanking.CollapseByDocIdWithTail"/>
    /// instead of plain max-passage — the tail-sum credit for a document's next-best chunks (spec §2).
    /// Key-map resolution and unresolved-parent handling live once here; the 3-arg overload above
    /// delegates to this one at <c>beta: 0</c>, which reaches <see cref="DocumentRanking.CollapseByDocId"/>
    /// through that method's own short-circuit rather than duplicating it.
    /// </summary>
    public static ChunkAggregation Aggregate(
        IEnumerable<(string ParentKey, double Score)> chunks,
        IReadOnlyDictionary<string, string> keyMap,
        int limit,
        double beta)
    {
        var resolved   = new List<(string DocId, double Score)>();
        var unresolved = new List<string>();

        foreach (var (parentKey, score) in chunks)
        {
            if (keyMap.TryGetValue(parentKey, out var docId))
                resolved.Add((docId, score));
            else
                unresolved.Add(parentKey);
        }

        return new ChunkAggregation(DocumentRanking.CollapseByDocIdWithTail(resolved, limit, beta), unresolved);
    }

    public static WinningChunkAggregation Aggregate(
        IEnumerable<(string ParentKey, double Score, string Text)> chunks,
        IReadOnlyDictionary<string, string> keyMap,
        int limit)
    {
        var resolved   = new List<(string DocId, double Score, string Text)>();
        var unresolved = new List<string>();

        foreach (var (parentKey, score, text) in chunks)
        {
            if (keyMap.TryGetValue(parentKey, out var docId))
                resolved.Add((docId, score, text));
            else
                unresolved.Add(parentKey);
        }

        return new WinningChunkAggregation(DocumentRanking.CollapseByDocId(resolved, limit), unresolved);
    }
}
