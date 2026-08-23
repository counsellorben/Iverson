using Iverson.Client.Attributes;

namespace Iverson.Client.Sample.Models;

[IversonEntity]
public class User
{
    [IversonKey]
    public Guid Id { get; set; }

    public string Name      { get; set; } = string.Empty;
    public string Email     { get; set; } = string.Empty;
    public string Username  { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    [OneToMany(typeof(UserArticle))]  // convention: FK on UserArticle = "UserId"
    public List<UserArticle> UserArticles { get; set; } = [];
}
