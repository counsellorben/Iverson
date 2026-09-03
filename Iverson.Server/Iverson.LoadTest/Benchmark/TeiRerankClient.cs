using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iverson.LoadTest.Benchmark;

/// <summary>What TEI's <c>/info</c> reports about the loaded model (spec A13, §4 startup guard).</summary>
public sealed record TeiInfo(string ModelId, int MaxInputLength, bool AutoTruncate);

/// <summary>
/// Text Embeddings Inference (TEI) <c>/rerank</c> over one <see cref="HttpClient"/> whose
/// <c>BaseAddress</c> is the reranker. Fail loud (spec §6): a non-success status, a timeout, or a
/// response that does not carry exactly one score per text throws — there is no
/// degrade-to-fused-order path, because a silent fallback writes a run file indistinguishable from a
/// reranked one.
/// </summary>
public sealed class TeiRerankClient(HttpClient http)
{
    // TEI reports max_batch_requests: 8 and max_client_batch_size: 32; 8 is what spec §9 measured.
    public const int BatchSize = 8;

    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private sealed record RerankRequest(string Query, IReadOnlyList<string> Texts, bool RawScores);
    private sealed record RerankRow(int Index, double Score);
    private sealed record InfoResponse(
        [property: JsonPropertyName("model_id")]         string ModelId,
        [property: JsonPropertyName("max_input_length")] int    MaxInputLength,
        [property: JsonPropertyName("auto_truncate")]    bool   AutoTruncate);

    public async Task<TeiInfo> GetInfoAsync(CancellationToken ct)
    {
        var info = await http.GetFromJsonAsync<InfoResponse>("info", ct)
                   ?? throw new InvalidOperationException("TEI /info returned an empty body.");
        return new TeiInfo(info.ModelId, info.MaxInputLength, info.AutoTruncate);
    }

    /// <summary>One cross-encoder score per text, in the order of <paramref name="texts"/>.</summary>
    public async Task<IReadOnlyList<double>> ScoreAsync(string query, IReadOnlyList<string> texts, CancellationToken ct)
    {
        var scores = new double[texts.Count];
        for (var offset = 0; offset < texts.Count; offset += BatchSize)
        {
            var batch = texts.Skip(offset).Take(BatchSize).ToList();
            using var response = await http.PostAsJsonAsync("rerank", new RerankRequest(query, batch, RawScores: false), SerializerOptions, ct);
            response.EnsureSuccessStatusCode();

            // TEI returns the batch sorted by score descending; `index` is the position within
            // the batch it was asked to score, so scores are mapped back by index, never by order.
            var rows = await response.Content.ReadFromJsonAsync<List<RerankRow>>(SerializerOptions, ct)
                       ?? throw new InvalidOperationException("TEI /rerank returned an empty body.");
            if (rows.Count != batch.Count || rows.Select(r => r.Index).Distinct().Count() != batch.Count
                || rows.Any(r => r.Index < 0 || r.Index >= batch.Count))
                throw new InvalidOperationException(
                    $"TEI /rerank scored a batch of {batch.Count} texts with {rows.Count} score(s) " +
                    $"(indices: {string.Join(",", rows.Select(r => r.Index))}).");
            foreach (var row in rows)
                scores[offset + row.Index] = row.Score;
        }
        return scores;
    }
}
