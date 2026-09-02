namespace Iverson.Client.Attributes;

/// <summary>
/// Declares the embedding model for every embedded and chunked property of this type. Optional;
/// omitted means the deployment's default model. Class-level, not per-property: one model per
/// type is what keeps a query from fusing across two incompatible vector spaces.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class IversonEmbeddingModelAttribute(string modelId) : Attribute
{
    public string ModelId { get; } = modelId;
}
