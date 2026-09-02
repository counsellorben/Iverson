using Iverson.Client.Attributes;

namespace Iverson.Client.Conformance.Driver.Models;

/// <summary>
/// S12 <c>model-inherited</c>'s .NET declaring parent. Field-less and never registered — it exists
/// only to carry <c>[IversonEmbeddingModel("nomic-embed-text")]</c> for <see cref="S12InheritedDotnet"/>
/// to inherit. It deliberately has no <c>[IversonEntity]</c>: the parent is never itself a
/// registrable type, so nothing exercises its own schema.
/// </summary>
[IversonEmbeddingModel("nomic-embed-text")]
public abstract class S12DeclaredDotnet
{
}

/// <summary>
/// S12 <c>model-inherited</c>'s .NET fixture (<c>register_inherited_doc</c> driver step). Declares
/// nothing of its own — it inherits <c>[IversonEmbeddingModel("nomic-embed-text")]</c> from
/// <see cref="S12DeclaredDotnet"/>, now that <c>IversonEmbeddingModelAttribute</c> is
/// <c>Inherited = true</c>. Must be named exactly <c>S12InheritedDotnet</c>: T8 derives and asserts
/// this name with ordinal comparison.
/// </summary>
[IversonEntity]
public class S12InheritedDotnet : S12DeclaredDotnet
{
    [IversonKey] public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;

    [IversonEmbedding] public string Title { get; set; } = string.Empty;
    [IversonChunk] public string Body { get; set; } = string.Empty;
}
