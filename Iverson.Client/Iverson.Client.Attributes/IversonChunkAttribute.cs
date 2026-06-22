namespace Iverson.Client.Attributes;

/// <summary>
/// Marks a string property as a source for chunk-level vector embeddings.
/// The server splits the field value into overlapping windows and stores each
/// chunk as a separate Qdrant point in a {collection}_chunks collection,
/// enabling passage-level RAG retrieval.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class IversonChunkAttribute : Attribute
{
    public EmbeddingModel Model     { get; }
    public int            Dimension { get; }
    public int            MaxTokens { get; }
    public int            Overlap   { get; }

    public IversonChunkAttribute(
        EmbeddingModel model,
        int maxTokens = 512,
        int overlap   = 64,
        int dimension = 0)
    {
        Model     = model;
        MaxTokens = maxTokens;
        Overlap   = overlap;
        Dimension = model == EmbeddingModel.Custom
            ? (dimension > 0
                ? dimension
                : throw new ArgumentException(
                    "A positive dimension is required when using EmbeddingModel.Custom.", nameof(dimension)))
            : model.GetDimension();
    }
}
