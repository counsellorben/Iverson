namespace Iverson.Client.Attributes;

/// <summary>
/// Marks a string property as a source for chunk-level vector embeddings.
/// The server splits the field value into overlapping windows and stores each
/// chunk as a separate Qdrant point in a {collection}_chunks collection,
/// enabling passage-level RAG retrieval.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class IversonChunkAttribute(int maxTokens = 512, int overlap = 64) : Attribute
{
    public int MaxTokens { get; } = maxTokens;
    public int Overlap   { get; } = overlap;
}
