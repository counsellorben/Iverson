namespace Iverson.Embeddings;

public interface IEmbeddingService
{
    int           Dimension { get; }
    string        ModelId   { get; }
    Task          InitializeAsync(CancellationToken ct = default);
    Task          EnsureInitializedAsync(CancellationToken ct = default);
    Task<float[]> EmbedDocumentAsync(string text, CancellationToken ct = default);
    Task<float[]> EmbedQueryAsync(string text, CancellationToken ct = default);
}
