namespace Iverson.Embeddings;

public sealed class EmbeddingServiceOptions
{
    public const string Section = "Embeddings";
    public string  BaseUrl        { get; set; } = "http://localhost:8091";
    public string  ModelId        { get; set; } = "BAAI/bge-base-en-v1.5";

    // 100 s is HttpClient's default, so the default changes nothing. Operators raise it (Embeddings__Timeout)
    // when a long document's embedding keeps timing out and halting the Intelligence consumer.
    public TimeSpan Timeout       { get; set; } = TimeSpan.FromSeconds(100);

    // null means "derive from ModelId"; "" means "deliberately no prefix". These are different:
    // arctic's document prefix IS the empty string, so "" cannot double as unset.
    public string? DocumentPrefix { get; set; }
    public string? QueryPrefix    { get; set; }

    // Per-model backends, bound from Embeddings__Models__N__Name / __BaseUrl (mirrors the Helm
    // global.embeddingModels list). A model with no entry uses BaseUrl, so an empty list is
    // today's behaviour exactly.
    public List<ModelEndpoint> Models { get; set; } = [];

    public string BaseUrlFor(string modelId) =>
        Models.FirstOrDefault(m => string.Equals(m.Name, modelId, StringComparison.Ordinal))?.BaseUrl
        ?? BaseUrl;
}

public sealed class ModelEndpoint
{
    public string Name    { get; set; } = "";
    public string BaseUrl { get; set; } = "";
}
