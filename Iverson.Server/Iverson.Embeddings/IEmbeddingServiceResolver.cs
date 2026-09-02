namespace Iverson.Embeddings;

public interface IEmbeddingServiceResolver
{
    /// <summary>
    /// The service for <paramref name="modelId"/>, cached per model. Null or empty resolves to
    /// the configured default — which is what "" means on the wire.
    /// </summary>
    IEmbeddingService Get(string? modelId);
}
