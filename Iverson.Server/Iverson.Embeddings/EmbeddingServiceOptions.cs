namespace Iverson.Embeddings;

public sealed class EmbeddingServiceOptions
{
    public const string Section = "Embeddings";
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string ModelId { get; set; } = "nomic-embed-text";
}
