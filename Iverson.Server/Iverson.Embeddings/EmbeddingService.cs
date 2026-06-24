using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iverson.Embeddings;

public sealed class EmbeddingService(
    IHttpClientFactory httpClientFactory,
    IOptions<EmbeddingServiceOptions> options,
    ILogger<EmbeddingService> logger) : IEmbeddingService
{
    private static readonly JsonSerializerOptions _jsonOpts =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private int _dimension;

    public int Dimension => _dimension > 0
        ? _dimension
        : throw new InvalidOperationException(
            "EmbeddingService not initialized — call InitializeAsync first.");

    public string ModelId => options.Value.ModelId;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var probe = await EmbedAsync("probe", ct);
        _dimension = probe.Length;
        logger.LogInformation(
            "EmbeddingService initialized: model={Model} dimension={Dimension}",
            ModelId, _dimension);
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        using var activity = Telemetry.Source.StartActivity("embeddings.embed", ActivityKind.Client);
        activity?.SetTag("embedding.model", ModelId);
        activity?.SetTag("embedding.input_chars", text.Length);

        try
        {
            using var client = httpClientFactory.CreateClient(Telemetry.HttpClientName);

            var body = JsonSerializer.Serialize(
                new { model = ModelId, input = text }, _jsonOpts);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/embeddings")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            var response = await client.SendAsync(request, ct);
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
            logger.LogError(ex, "EmbedAsync failed for model {Model}", ModelId);
            throw;
        }
    }
}
