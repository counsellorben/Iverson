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
    private const int TailCap = 3;

    public static IReadOnlyList<(string DocId, double Score)> CollapseByDocId(
        IEnumerable<(string DocId, double Score)> scored,
        int limit) =>
        CollapseByDocId(scored.Select(s => (s.DocId, s.Score, Text: "")), limit)
            .Select(r => (r.DocId, r.Score))
            .ToList();

    /// <summary>
    /// Same collapse, carrying a per-row payload — the chunk text — and keeping the payload of the
    /// row that supplied the maximum. Phase 1 of the reranker needs the winning chunk itself, not
    /// only its score (spec §3.3, A26); the 2-tuple overload above delegates here so there is one
    /// max-tracking rule (first-seen wins an exact tie).
    /// </summary>
    public static IReadOnlyList<(string DocId, double Score, string Text)> CollapseByDocId(
        IEnumerable<(string DocId, double Score, string Text)> scored,
        int limit)
    {
        var maxByDoc = new Dictionary<string, (double Score, string Text)>();

        foreach (var (docId, score, text) in scored)
        {
            if (!maxByDoc.TryGetValue(docId, out var existing) || score > existing.Score)
                maxByDoc[docId] = (score, text);
        }

        return maxByDoc
            .OrderByDescending(kv => kv.Value.Score)
            .Take(limit)
            .Select(kv => (kv.Key, kv.Value.Score, kv.Value.Text))
            .ToList();
    }

    /// <summary>
    /// Collapses chunk rows to one row per parent, scoring each as its maximum chunk score plus
    /// <paramref name="beta"/> times the sum of up to its next <see cref="TailCap"/> highest chunk
    /// scores (spec §2). At beta == 0 this delegates to <see cref="CollapseByDocId"/> outright, so the
    /// ranking is bit-identical to today's max-passage rather than incidentally equal through IEEE
    /// arithmetic — the Phase 1 identity check asserts exactly that.
    /// </summary>
    public static IReadOnlyList<(string DocId, double Score)> CollapseByDocIdWithTail(
        IEnumerable<(string DocId, double Score)> scored,
        int limit,
        double beta)
    {
        if (beta == 0) return CollapseByDocId(scored, limit);

        var byDoc = new Dictionary<string, List<double>>();
        foreach (var (docId, score) in scored)
        {
            if (!byDoc.TryGetValue(docId, out var scores))
                byDoc[docId] = scores = [];
            scores.Add(score);
        }

        return byDoc
            .Select(kv =>
            {
                var descending = kv.Value.OrderByDescending(s => s).ToList();
                var tail       = descending.Skip(1).Take(TailCap).Sum();
                return (DocId: kv.Key, Score: descending[0] + beta * tail);
            })
            .OrderByDescending(r => r.Score)
            .Take(limit)
            .ToList();
    }
}
