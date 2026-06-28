using Iverson.Client.Attributes;

namespace Iverson.Client.Sample.Models;

[IversonEntity]
public class Tag
{
    [IversonKey]
    public Guid Id { get; set; }

    public string Label { get; set; } = string.Empty;
    public string Slug  { get; set; } = string.Empty;

    [ManyToMany(typeof(Article))]  // convention: join key in payload = "ArticleIds"
    public List<Article> Articles { get; set; } = [];
}
