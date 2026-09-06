namespace Iverson.LoadTest.Benchmark;

/// <summary>Which text each of the 50 max-passage documents sends to the cross-encoder (spec §3.3).</summary>
public enum RerankInput
{
    /// <summary>The document's winning chunk, as Phase 1 scored (arm A1).</summary>
    WinningChunk,
    /// <summary>The document's full <c>corpus.jsonl</c> text — title + abstract, as the published BEIR setup (arm A4).</summary>
    Document,
}

public static class RerankInputs
{
    public const string WinningChunkFlag = "winning-chunk";
    public const string DocumentFlag     = "document";

    /// <summary>
    /// Parses <c>--rerank-input</c>. An unknown value is refused (a silent default would run a mislabelled
    /// arm), and <c>document</c> without <c>--rerank-url</c> is refused: an input mode for a reranker that
    /// is not there is a mislabelled arm too (spec §3.1).
    /// </summary>
    public static RerankInput Parse(string value, string rerankUrl)
    {
        var mode = value switch
        {
            WinningChunkFlag => RerankInput.WinningChunk,
            DocumentFlag     => RerankInput.Document,
            _ => throw new InvalidOperationException(
                $"--rerank-input '{value}' is not one of '{WinningChunkFlag}', '{DocumentFlag}'."),
        };
        if (mode == RerankInput.Document && string.IsNullOrWhiteSpace(rerankUrl))
            throw new InvalidOperationException("--rerank-input document was given without --rerank-url.");
        return mode;
    }

    /// <summary>
    /// The texts to score, one per winner, in <paramref name="winners"/> order — the caller re-pairs
    /// scores positionally. In <see cref="RerankInput.Document"/> mode a winner absent from the corpus
    /// map throws (fail loud, spec §4); it is never scored through a fallback text.
    /// </summary>
    public static IReadOnlyList<string> Select(
        RerankInput mode,
        IReadOnlyList<(string DocId, double Score, string Text)> winners,
        IReadOnlyDictionary<string, string>? corpusText)
    {
        if (mode == RerankInput.WinningChunk)
            return winners.Select(w => w.Text).ToList();

        if (corpusText is null)
            throw new InvalidOperationException("--rerank-input document requires the corpus text map.");

        var texts = new List<string>(winners.Count);
        foreach (var w in winners)
        {
            if (!corpusText.TryGetValue(w.DocId, out var text))
                throw new InvalidOperationException(
                    $"DocId={w.DocId} is absent from corpus.jsonl -- this arm must not be scored.");
            texts.Add(text);
        }
        return texts;
    }
}
