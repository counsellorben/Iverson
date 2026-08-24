using Iverson.Client.Attributes;

namespace Iverson.Client.Conformance.Driver.Models;

/// <summary>
/// S4 <c>interop</c>'s root type. .NET (like Java) declares the foreign key as its own field
/// alongside an annotated navigation property; only <c>SharedAuthorId</c> is ever sent, per the
/// FK-only write contract.
/// </summary>
[IversonEntity]
public class SharedArticle
{
    [IversonKey] public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;

    public Guid SharedAuthorId { get; set; }

    [ManyToOne(typeof(SharedAuthor))] public SharedAuthor? SharedAuthor { get; set; }
}
