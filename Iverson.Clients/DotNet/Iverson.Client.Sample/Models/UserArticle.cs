using Iverson.Client.Attributes;

namespace Iverson.Client.Sample.Models;

[IversonEntity]
public class UserArticle
{
    [IversonKey]
    public Guid Id { get; set; }

    public Guid     UserId    { get; set; }  // convention: ManyToOne(User)    → "UserId"
    public Guid     ArticleId { get; set; }  // convention: ManyToOne(Article) → "ArticleId"
    public DateTime CreatedAt { get; set; }

    [ManyToOne(typeof(User))]
    public User? User { get; set; }

    [ManyToOne(typeof(Article))]
    public Article? Article { get; set; }
}
