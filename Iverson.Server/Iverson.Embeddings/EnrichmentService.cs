using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iverson.Embeddings;

public sealed class EnrichmentService(
    IHttpClientFactory httpClientFactory,
    IOptions<EnrichmentServiceOptions> options,
    ILogger<EnrichmentService> logger) : IEnrichmentService
{
    // The chat API needs an explicit completion budget. The enrichment prompts ask for 2-3
    // sentences, 5-10 keywords, a 1-2 sentence description, or one JSON object.
    internal const int MaxGeneratedTokens = 256;

    private static readonly JsonSerializerOptions _jsonOpts =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly Regex FencedBlock =
        new("```(?:json)?\\s*\\n(.*?)\\n\\s*```", RegexOptions.Singleline | RegexOptions.Compiled);

    // Absolute, from this service's own BaseUrl: the named HttpClient's BaseAddress is not the
    // request base (the same rule EmbeddingService follows).
    private readonly string _baseUrl = options.Value.BaseUrl;

    public string ModelId => options.Value.ModelId;

    public Task<string> GenerateAsync(string prompt, CancellationToken ct = default) =>
        GenerateInternalAsync(prompt, jsonFormat: false, ct);

    public async Task<string> GenerateJsonAsync(string prompt, CancellationToken ct = default) =>
        ExtractJson(await GenerateInternalAsync(prompt, jsonFormat: true, ct));

    private async Task<string> GenerateInternalAsync(string prompt, bool jsonFormat, CancellationToken ct)
    {
        using var activity = Telemetry.Source.StartActivity("enrichment.generate", ActivityKind.Client);
        activity?.SetTag("enrichment.model", ModelId);
        activity?.SetTag("enrichment.input_chars", prompt.Length);
        activity?.SetTag("enrichment.json_format", jsonFormat);

        try
        {
            using var client = httpClientFactory.CreateClient(Telemetry.EnrichmentHttpClientName);

            // The OpenAI-compatible chat route, served by TGI and by Ollama alike. No grammar for the
            // JSON case: TGI's grammars need a fixed property set and the extraction hint is free
            // text (spec §3.5); ExtractJson isolates the object from the reply instead.
            var body = JsonSerializer.Serialize(new
            {
                model       = ModelId,
                messages    = new[] { new { role = "user", content = prompt } },
                max_tokens  = MaxGeneratedTokens,
                temperature = 0,
                stream      = false,
            }, _jsonOpts);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_baseUrl), "/v1/chat/completions"))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            await using var responseStream = await response.Content.ReadAsStreamAsync(ct);
            using var doc                  = await JsonDocument.ParseAsync(responseStream, default, ct);

            // { "choices": [ { "message": { "role": "assistant", "content": "..." } } ], ... }
            var text = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;

            activity?.SetTag("enrichment.output_chars", text.Length);
            activity?.SetStatus(ActivityStatusCode.Ok);

            return text;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logger.LogError(ex, "GenerateAsync failed for model {Model}", ModelId);
            throw;
        }
    }

    // The first fenced code block if the reply carries one, else the first balanced { ... } span
    // (string-aware), parsed to prove it is JSON. Without a format directive Ollama wraps the object
    // in prose on both sides and TGI fences it (spec §9 rows 12, 17); a leading/trailing fence strip
    // isolates neither.
    internal static string ExtractJson(string text)
    {
        var fence     = FencedBlock.Match(text);
        var candidate = fence.Success ? fence.Groups[1].Value.Trim() : BalancedObject(text);
        if (candidate is not null)
        {
            try
            {
                using var _ = JsonDocument.Parse(candidate);
                return candidate;
            }
            catch (JsonException) { }
        }

        var head = text.Length <= 200 ? text : text[..200];
        throw new InvalidOperationException(
            $"Enrichment backend returned no parseable JSON object for a JSON extraction: '{head}'");
    }

    private static string? BalancedObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0) return null;

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return text.Substring(start, i - start + 1);
        }
        return null;
    }
}
