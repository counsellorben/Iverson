namespace Iverson.LoadTest.Benchmark;

/// <summary>
/// Collapses a stream of chunk-level search results down to one row per parent document
/// (max-passage aggregation): the parent's score is the maximum score among its chunks, not
/// the first chunk seen or the sum of its chunks. <c>SearchChunksRequest.top_k</c> counts
/// chunks and the server does not dedup by parent (spec A22), so this is what turns a
/// chunk-budget-sized result set back into a document-ranked list comparable to SearchSimilar's.
/// </summary>
public static class MaxPassageAggregator
{
    public static IReadOnlyList<(string DocId, double Score)> Aggregate(
        IEnumerable<(string ParentKey, double Score)> chunks,
        IReadOnlyDictionary<string, string> keyMap,
        int limit)
    {
        var maxByParent = new Dictionary<string, double>();

        foreach (var (parentKey, score) in chunks)
        {
            if (!keyMap.TryGetValue(parentKey, out var docId))
                throw new InvalidOperationException(
                    $"Chunk result's ParentKey '{parentKey}' is not present in the key map — " +
                    "ingest and query disagree about the corpus.");

            if (!maxByParent.TryGetValue(docId, out var existing) || score > existing)
                maxByParent[docId] = score;
        }

        return maxByParent
            .OrderByDescending(kv => kv.Value)
            .Take(limit)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }
}
