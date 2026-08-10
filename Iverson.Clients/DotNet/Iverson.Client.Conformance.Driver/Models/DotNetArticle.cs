using Iverson.Client.Attributes;

namespace Iverson.Client.Conformance.Driver.Models;

/// <summary>
/// S1's root type. .NET (like Java) declares each foreign key as its own field alongside an
/// annotated navigation property; the write contract is foreign-key only, so only the
/// <c>DotNetAuthorId</c>/<c>DotNetTagIds</c> fields are ever sent.
/// </summary>
[IversonEntity]
public class DotNetArticle
{
    [IversonKey] public Guid Id { get; set; }
    [IversonTenant] public string TenantId { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;

    public Guid DotNetAuthorId { get; set; }
    public Guid[] DotNetTagIds { get; set; } = [];

    [ManyToOne(typeof(DotNetAuthor))] public DotNetAuthor? DotNetAuthor { get; set; }
    [ManyToMany(typeof(DotNetTag))] public List<DotNetTag> DotNetTags { get; set; } = [];
}
