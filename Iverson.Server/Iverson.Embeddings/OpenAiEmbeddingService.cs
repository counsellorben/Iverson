using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iverson.Embeddings;

public sealed class OpenAiEmbeddingService(
    IHttpClientFactory httpClientFactory,
    IOptions<EmbeddingServiceOptions> opts,
    ILogger<OpenAiEmbeddingService> logger) : IEmbeddingService
{
    private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<float[]> EmbedAsync(string text, string modelId, CancellationToken ct = default)
    {
        using var activity = Telemetry.Source.StartActivity("embeddings.embed", ActivityKind.Client);
        activity?.SetTag("embedding.model", modelId);
        activity?.SetTag("embedding.input_chars", text.Length);

        try
        {
            using var http = httpClientFactory.CreateClient(Telemetry.HttpClientName);

            var body    = JsonSerializer.Serialize(new { model = modelId, input = text }, _jsonOpts);
            using var msg = new HttpRequestMessage(HttpMethod.Post, "/v1/embeddings")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opts.Value.ApiKey);

            var response = await http.SendAsync(msg, ct);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            using var doc    = JsonDocument.Parse(responseJson);

            var embedding = doc.RootElement
                .GetProperty("data")[0]
                .GetProperty("embedding")
                .EnumerateArray()
                .Select(e => (float)e.GetDouble())
                .ToArray();

            activity?.SetTag("embedding.output_dims", embedding.Length);
            activity?.SetStatus(ActivityStatusCode.Ok);

            return embedding;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logger.LogError(ex, "EmbedAsync failed for model {Model}", modelId);
            throw;
        }
    }
}
