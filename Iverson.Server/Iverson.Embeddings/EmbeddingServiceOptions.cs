namespace Iverson.Embeddings;

public sealed class EmbeddingServiceOptions
{
    public const string Section = "Embeddings";
    public string  BaseUrl        { get; set; } = "http://localhost:11434";
    public string  ModelId        { get; set; } = "nomic-embed-text";

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
