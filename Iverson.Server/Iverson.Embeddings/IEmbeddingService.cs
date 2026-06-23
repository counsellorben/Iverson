namespace Iverson.Embeddings;

public interface IEmbeddingService
{
    int    Dimension { get; }
    string ModelId   { get; }
    Task   InitializeAsync(CancellationToken ct = default);
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
}
