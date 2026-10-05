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

    // NonBacktracking (CSR round-10 #21): the lazy body over many unterminated fence openings was
    // quadratic under the backtracking engine; this engine is linear and returns the same groups.
    private static readonly Regex FencedBlock =
        new("```(?:json)?\\s*\\n(.*?)\\n\\s*```", RegexOptions.Singleline | RegexOptions.NonBacktracking);

    // A completion capped at MaxGeneratedTokens is a few KB; anything near this cap is not a reply.
    internal const int MaxResponseBytes = 1024 * 1024;

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

        // The body is read after the headers, outside HttpClient.Timeout, so the whole call shares one
        // deadline: the same bound HttpClient.Timeout gave the buffered read before (CSR round-10 #21).
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(options.Value.Timeout);

        try
        {
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

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                response.EnsureSuccessStatusCode();

                using var doc = JsonDocument.Parse(await ReadCappedAsync(response.Content, deadline.Token));

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
            catch (OperationCanceledException ex) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // The shape HttpClient itself raises on timeout, which EnrichmentConsumer skips rather
                // than redelivers. Cancelling the linked source surfaces with an inner IOException,
                // which that filter would not match.
                throw new TaskCanceledException(
                    $"The enrichment call exceeded its {options.Value.Timeout} timeout.", new TimeoutException(ex.Message, ex));
            }
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logger.LogError(ex, "GenerateAsync failed for model {Model}", ModelId);
            throw;
        }
    }

    private static async Task<byte[]> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > MaxResponseBytes)
            throw Oversize();

        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
                throw Oversize();
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    // Non-transient (CSR round-10 #21): an oversize reply is the backend misbehaving, not an outage.
    private static InvalidOperationException Oversize() =>
        new($"Enrichment backend response exceeded {MaxResponseBytes} bytes.");

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
