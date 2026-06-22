namespace Iverson.Embeddings;

public interface IEmbeddingService
{
    /// <summary>Embeds a single text using the specified model and returns the float vector.</summary>
    Task<float[]> EmbedAsync(string text, string modelId, CancellationToken ct = default);
}
