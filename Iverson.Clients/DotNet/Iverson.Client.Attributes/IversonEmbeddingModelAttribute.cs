namespace Iverson.Client.Attributes;

/// <summary>
/// Declares the embedding model for every embedded and chunked property of this type. Optional;
/// omitted means the deployment's default model, unless a base class declares one — a derived
/// class with no declaration of its own inherits its nearest ancestor's, and a derived class
/// that declares its own overrides the inherited one. Class-level, not per-property: one model
/// per type is what keeps a query from fusing across two incompatible vector spaces.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class IversonEmbeddingModelAttribute(string modelId) : Attribute
{
    public string ModelId { get; } = modelId;
}
