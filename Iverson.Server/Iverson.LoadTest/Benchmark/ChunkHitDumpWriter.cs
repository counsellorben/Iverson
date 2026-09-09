using System.Globalization;

namespace Iverson.LoadTest.Benchmark;

/// <summary>
/// Dumps the raw, per-query SearchChunks hit list -- before <see cref="MaxPassageAggregator"/> collapses
/// it to one row per document -- as a tab-separated file: <c>queryId parentKey rank score</c>, rank
/// 1-based per query, rows in the order the hits were supplied (no sorting -- a later task byte-compares
/// a run rebuilt from this dump against the original, and that comparison depends on row order being
/// exactly reproducible). This is machine-read intermediate state for an offline replay at different
/// weightings, not a scored artefact, so scores are written shortest-round-trippable
/// (<c>score.ToString(CultureInfo.InvariantCulture)</c>) rather than <see cref="TrecRunWriter"/>'s
/// display-formatted <c>F6</c> -- <c>F6</c> would collapse genuinely distinct near-tied scores into
/// exact ties.
/// </summary>
public static class ChunkHitDumpWriter
{
    public static async Task WriteAsync(
        string path,
        IEnumerable<(string QueryId, IReadOnlyList<(string ParentKey, double Score)> Hits)> results,
        CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await using var writer = new StreamWriter(path, append: false);
        await writer.WriteLineAsync("queryId\tparentKey\trank\tscore");

        foreach (var (queryId, hits) in results)
        {
            for (var i = 0; i < hits.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (parentKey, score) = hits[i];
                var rank = i + 1;
                await writer.WriteLineAsync(FormattableString.Invariant(
                    $"{queryId}\t{parentKey}\t{rank}\t{score.ToString(CultureInfo.InvariantCulture)}"));
            }
        }
    }
}
