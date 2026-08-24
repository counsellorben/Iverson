using Iverson.Client.Attributes;

namespace Iverson.Client.Conformance.Driver.Models;

/// <summary>
/// S1's "one" side. Carries the reverse <see cref="OneToManyAttribute"/> navigation the
/// foreign-key-only write contract work broke, so the harness observes it end to end.
/// </summary>
[IversonEntity]
public class DotNetAuthor
{
    [IversonKey] public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    [OneToMany(typeof(DotNetArticle))] public List<DotNetArticle> DotNetArticles { get; set; } = [];
}
