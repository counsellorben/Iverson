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
}
