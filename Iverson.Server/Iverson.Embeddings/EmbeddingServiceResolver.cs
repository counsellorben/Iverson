using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iverson.Embeddings;

public sealed class EmbeddingServiceResolver(
    IHttpClientFactory httpClientFactory,
    IOptions<EmbeddingServiceOptions> options,
    IEmbeddingService defaultService,
    ILogger<EmbeddingService> serviceLogger) : IEmbeddingServiceResolver
{
    private readonly ConcurrentDictionary<string, IEmbeddingService> _byModel = new(StringComparer.Ordinal);

    public IEmbeddingService Get(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId) ||
            string.Equals(modelId, options.Value.ModelId, StringComparison.Ordinal))
            return defaultService;

        return _byModel.GetOrAdd(modelId, m => new EmbeddingService(
            httpClientFactory,
            // DocumentPrefix/QueryPrefix are deliberately NOT copied. The configured overrides
            // are shaped for the DEFAULT model, and stamping a nomic-shaped prefix onto arctic's
            // embeddings is exactly the misconfiguration EmbeddingPrefixes exists to prevent.
            // Left null, the field initializers derive this model's own pair from the table.
            Options.Create(new EmbeddingServiceOptions { BaseUrl = options.Value.BaseUrl, ModelId = m }),
            serviceLogger));
    }
}
