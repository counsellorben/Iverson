using System.Diagnostics;
using System.Net;
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
    private readonly SemaphoreSlim _initLock = new(1, 1);
    // Resolved through Models so the DI-constructed default service reaches the backend that
    // serves its model when that model is listed (a benchmark arm makes a TEI-only model the
    // default; the resolver never consults the list for the default id).
    private readonly string _baseUrl = options.Value.BaseUrlFor(options.Value.ModelId);

    private readonly string _documentPrefix =
        options.Value.DocumentPrefix ?? EmbeddingPrefixes.For(options.Value.ModelId).Document;
    private readonly string _queryPrefix =
        options.Value.QueryPrefix ?? EmbeddingPrefixes.For(options.Value.ModelId).Query;

    public int Dimension => _dimension > 0
        ? _dimension
        : throw new InvalidOperationException(
            "EmbeddingService not initialized — call InitializeAsync first.");

    public string ModelId => options.Value.ModelId;

    public Task InitializeAsync(CancellationToken ct = default) => EnsureInitializedAsync(ct);

    public async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_dimension > 0) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_dimension > 0) return;
            var probe = await EmbedAsync("probe", ct);
            // Before _dimension is recorded (spec §3.2): a mismatch must leave the service
            // uninitialised so every later call re-runs the guard and re-throws, instead of
            // returning at the top. The startup caller swallows this exception; schema
            // registration is the caller that turns it into Unavailable and blocks the arm.
            await VerifyServedModelAsync(ct);
            _dimension = probe.Length;
            logger.LogInformation(
                "EmbeddingService initialized: model={Model} dimension={Dimension} documentPrefix={DocPrefix} queryPrefix={QueryPrefix}",
                ModelId, _dimension, _documentPrefix, _queryPrefix);
        }
        finally
        {
            _initLock.Release();
        }
    }

    // TEI serves one model per container and ignores the request's "model" field, so the dimension
    // probe cannot tell bge-base from nomic (both 768). TEI answers GET /info with the served
    // model_id; Ollama answers 404 there, which makes this a no-op on Ollama.
    private async Task VerifyServedModelAsync(CancellationToken ct)
    {
        using var client   = httpClientFactory.CreateClient(Telemetry.HttpClientName);
        using var request  = new HttpRequestMessage(HttpMethod.Get, EndpointUri("/info"));
        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode != HttpStatusCode.OK) return;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc          = await JsonDocument.ParseAsync(stream, default, ct);
        if (!doc.RootElement.TryGetProperty("model_id", out var served)) return;

        var servedId = served.GetString();
        if (!string.Equals(servedId, ModelId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Embedding backend at {_baseUrl} serves model '{servedId}' but this service is " +
                $"configured for '{ModelId}'.");
    }

    // Absolute, from this service's own base URL: services for different models share the named
    // HttpClient (handler + telemetry) but not its BaseAddress.
    private Uri EndpointUri(string path) => new(new Uri(_baseUrl), path);

    private async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        using var activity = Telemetry.Source.StartActivity("embeddings.embed", ActivityKind.Client);
        activity?.SetTag("embedding.model", ModelId);
        activity?.SetTag("embedding.input_chars", text.Length);

        try
        {
            using var client = httpClientFactory.CreateClient(Telemetry.HttpClientName);

            var body = JsonSerializer.Serialize(
                new { model = ModelId, input = text }, _jsonOpts);
            using var request = new HttpRequestMessage(HttpMethod.Post, EndpointUri("/v1/embeddings"))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            await using var responseStream = await response.Content.ReadAsStreamAsync(ct);
            using var doc                  = await JsonDocument.ParseAsync(responseStream, default, ct);

            // /v1/embeddings returns { "data": [ { "embedding": [...] } ] } on both Ollama and TEI
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

    internal static string ComposeDocumentInput(string prefix, string text) => prefix + text;
    internal static string ComposeQueryInput(string prefix, string text)    => prefix + text;

    public Task<float[]> EmbedDocumentAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new EmptyEmbeddingInputException("Cannot embed empty or whitespace-only text.");
        return EmbedAsync(ComposeDocumentInput(_documentPrefix, text), ct);
    }

    public Task<float[]> EmbedQueryAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new EmptyEmbeddingInputException("Cannot embed empty or whitespace-only text.");
        return EmbedAsync(ComposeQueryInput(_queryPrefix, text), ct);
    }
}
