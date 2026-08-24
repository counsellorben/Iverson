using Iverson.Client.Attributes;

namespace Iverson.Client.Sample.Models;

[IversonEntity]
public class Author
{
    [IversonKey]
    public Guid Id { get; set; }

    public string Name  { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Bio   { get; set; } = string.Empty;

    [OneToMany(typeof(Article))]  // convention: FK on Article = "AuthorId"
    public List<Article> Articles { get; set; } = [];
}
