using Iverson.Client.Attributes;

namespace Iverson.Client.Sample.Models;

[IversonEntity]
public class Article
{
    [IversonKey]
    public Guid Id { get; set; }

    [IversonEmbedding]
    public string    Title       { get; set; } = string.Empty;

    [IversonChunk(maxTokens: 512, overlap: 64)]
    public string    Body        { get; set; } = string.Empty;

    public DateTime  PublishedAt { get; set; }
    public bool      IsPublished { get; set; }

    public Guid    AuthorId  { get; set; }  // convention: {RelatedTypeName}Id  → "AuthorId"
    public Guid[]  TagIds    { get; set; } = [];  // convention: {RelatedTypeName}Ids → "TagIds"

    [ManyToOne(typeof(Author))]
    public Author? Author { get; set; }

    [ManyToMany(typeof(Tag))]
    public List<Tag> Tags { get; set; } = [];

    [OneToMany(typeof(UserArticle))]
    public List<UserArticle> UserArticles { get; set; } = [];
}
