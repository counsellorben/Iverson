using Iverson.Client.Attributes;

namespace Iverson.Client.Conformance.Driver.Models;

/// <summary>
/// S1's root type. .NET (like Java) declares each foreign key as its own field alongside an
/// annotated navigation property; the write contract is foreign-key only, so only the
/// <c>DotNetAuthorId</c>/<c>DotNetTagIds</c>/<c>DotNetTagId</c> fields are ever sent.
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
    public Guid DotNetTagId { get; set; }

    [ManyToOne(typeof(DotNetAuthor))] public DotNetAuthor? DotNetAuthor { get; set; }
    [ManyToMany(typeof(DotNetTag))] public List<DotNetTag> DotNetTags { get; set; } = [];

    // IVC-REL-001/002/003's one_to_one fixture: a second relation to DotNetTag (the many_to_many
    // relation's own related type), through the SINGULAR "DotNetTagId" foreign key so it does not
    // collide with the many_to_many's plural "DotNetTagIds" — exercising the one_to_one kind end
    // to end without needing a whole new entity type.
    [OneToOne(typeof(DotNetTag))] public DotNetTag? DotNetTag { get; set; }
}
