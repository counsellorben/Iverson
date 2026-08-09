using Iverson.Client.Attributes;

namespace Iverson.Client.Sample.Models;

[IversonEntity]
public class Tag
{
    [IversonKey]
    public Guid Id { get; set; }

    public string Label { get; set; } = string.Empty;
    public string Slug  { get; set; } = string.Empty;

    [IversonTenant] public string TenantId { get; set; } = string.Empty;

    public Guid[] ArticleIds { get; set; } = [];  // convention: {RelatedTypeName}Ids → "ArticleIds"

    [ManyToMany(typeof(Article))]
    public List<Article> Articles { get; set; } = [];
}
