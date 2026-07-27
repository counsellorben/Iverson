using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iverson.Embeddings;

public sealed class EnrichmentService(
    IHttpClientFactory httpClientFactory,
    IOptions<EnrichmentServiceOptions> options,
    ILogger<EnrichmentService> logger) : IEnrichmentService
{
    private static readonly JsonSerializerOptions _jsonOpts =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string ModelId => options.Value.ModelId;

    public Task<string> GenerateAsync(string prompt, CancellationToken ct = default) =>
        GenerateInternalAsync(prompt, jsonFormat: false, ct);

    public Task<string> GenerateJsonAsync(string prompt, CancellationToken ct = default) =>
        GenerateInternalAsync(prompt, jsonFormat: true, ct);

    private async Task<string> GenerateInternalAsync(string prompt, bool jsonFormat, CancellationToken ct)
    {
        using var activity = Telemetry.Source.StartActivity("enrichment.generate", ActivityKind.Client);
        activity?.SetTag("enrichment.model", ModelId);
        activity?.SetTag("enrichment.input_chars", prompt.Length);
        activity?.SetTag("enrichment.json_format", jsonFormat);

        try
        {
            using var client = httpClientFactory.CreateClient(Telemetry.EnrichmentHttpClientName);

            object payload = jsonFormat
                ? new { model = ModelId, prompt, stream = false, format = "json" }
                : new { model = ModelId, prompt, stream = false };

            var body = JsonSerializer.Serialize(payload, _jsonOpts);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/generate")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            await using var responseStream = await response.Content.ReadAsStreamAsync(ct);
            using var doc                  = await JsonDocument.ParseAsync(responseStream, default, ct);

            // /api/generate with stream:false returns { "response": "..." }
            var text = doc.RootElement.GetProperty("response").GetString() ?? string.Empty;

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
}
