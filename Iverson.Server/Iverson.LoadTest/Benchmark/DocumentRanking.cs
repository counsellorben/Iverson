namespace Iverson.LoadTest.Benchmark;

/// <summary>
/// Collapses scored document rows to one row per document id, keeping the maximum score.
///
/// <para>Both run files need this, for different reasons. <see cref="MaxPassageAggregator"/> needs it
/// because <c>SearchChunks</c> returns several chunks of one parent (spec A22). <c>SearchSimilar</c>
/// needs it because two entities can carry the same <c>DocId</c> — the same corpus ingested twice
/// produces two points with distinct keys and one doc id, and a TREC run listing the same doc id at
/// two ranks is malformed: scorers either reject it or silently collapse it, so the ranking scored is
/// not the ranking produced.</para>
/// </summary>
public static class DocumentRanking
{
    public static IReadOnlyList<(string DocId, double Score)> CollapseByDocId(
        IEnumerable<(string DocId, double Score)> scored,
        int limit)
    {
        var maxByDoc = new Dictionary<string, double>();

        foreach (var (docId, score) in scored)
        {
            if (!maxByDoc.TryGetValue(docId, out var existing) || score > existing)
                maxByDoc[docId] = score;
        }

        return maxByDoc
            .OrderByDescending(kv => kv.Value)
            .Take(limit)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }
}
