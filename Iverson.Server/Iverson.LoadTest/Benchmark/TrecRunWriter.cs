using System.Globalization;

namespace Iverson.LoadTest.Benchmark;

/// <summary>
/// Writes a ranked result list in the standard TREC run-file format:
/// <c>qid Q0 docid rank score runtag</c>, space-separated, rank starting at 1. Scoring
/// (alpha-nDCG, nDCG, Recall) is external to the harness — this only writes rows.
/// </summary>
public static class TrecRunWriter
{
    public static async Task WriteAsync(
        string path,
        IEnumerable<(string QueryId, IReadOnlyList<(string DocId, double Score)> Ranked)> results,
        string runTag,
        CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await using var writer = new StreamWriter(path, append: false);
        foreach (var (queryId, ranked) in results)
        {
            for (var i = 0; i < ranked.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (docId, score) = ranked[i];
                var rank = i + 1;
                await writer.WriteLineAsync(FormattableString.Invariant(
                    $"{queryId} Q0 {docId} {rank} {score.ToString("F6", CultureInfo.InvariantCulture)} {runTag}"));
            }
        }
    }
}
