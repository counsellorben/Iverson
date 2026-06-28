using Iverson.Api.Schema;

namespace Iverson.Api.Tests.Helpers;

public static class SchemaFixtures
{
    // Author: no relations, no vector/chunk fields → Record + Engagement only
    public static SchemaDescriptor AuthorSchema() => new()
    {
        TypeName       = "Author",
        TableName      = "authors",
        CollectionName = null,
        KeyColumn      = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns  = [new ColumnDescriptor("Name", "text", false), new ColumnDescriptor("Bio", "text", true)],
        FkColumns      = [],
        VectorFields   = [],
        ChunkFields    = [],
        Relations      = []
    };

    // Article: ManyToOne(Author), vector on Title, chunk on Body → Record + Engagement + Intelligence
    public static SchemaDescriptor ArticleSchema() => new()
    {
        TypeName       = "Article",
        TableName      = "articles",
        CollectionName = "articles",
        KeyColumn      = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns  = [new ColumnDescriptor("Title", "text", false), new ColumnDescriptor("Body", "text", false)],
        FkColumns      = [new ForeignKeyDescriptor("AuthorId", "Author")],
        VectorFields   = [new VectorDescriptor("Title", 768, "nomic-embed-text")],
        ChunkFields    = [new ChunkDescriptor("Body", 512, 64, "nomic-embed-text", 768)],
        Relations      = [new RelationDescriptor("Author", RelationKind.ManyToOne, "Author", "AuthorId")]
    };

    // Article with a OneToMany — makes it NOT Engagement-eligible
    public static SchemaDescriptor ArticleWithOneToManySchema() => new()
    {
        TypeName       = "Article",
        TableName      = "articles",
        CollectionName = "articles",
        KeyColumn      = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns  = [new ColumnDescriptor("Title", "text", false)],
        FkColumns      = [new ForeignKeyDescriptor("AuthorId", "Author")],
        VectorFields   = [],
        ChunkFields    = [],
        Relations      = [
            new RelationDescriptor("Author",      RelationKind.ManyToOne, "Author",      "AuthorId"),
            new RelationDescriptor("UserArticles", RelationKind.OneToMany, "UserArticle", "ArticleId")
        ]
    };

    // UserArticle: two ManyToOne relations → Engagement eligible
    public static SchemaDescriptor UserArticleSchema() => new()
    {
        TypeName       = "UserArticle",
        TableName      = "user_articles",
        CollectionName = null,
        KeyColumn      = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns  = [],
        FkColumns      = [new ForeignKeyDescriptor("UserId", "User"), new ForeignKeyDescriptor("ArticleId", "Article")],
        VectorFields   = [],
        ChunkFields    = [],
        Relations      = [
            new RelationDescriptor("User",    RelationKind.ManyToOne, "User",    "UserId"),
            new RelationDescriptor("Article", RelationKind.ManyToOne, "Article", "ArticleId")
        ]
    };

    // Post with ManyToMany → Tags (for ResolveManyToManyAsync tests)
    public static SchemaDescriptor PostWithTagsSchema() => new()
    {
        TypeName       = "Post",
        TableName      = "posts",
        CollectionName = null,
        KeyColumn      = new ColumnDescriptor("Id",     "uuid", false),
        ScalarColumns  = [new ColumnDescriptor("Title", "text", false)],
        FkColumns      = [new ForeignKeyDescriptor("TagIds", "Tag")],
        VectorFields   = [],
        ChunkFields    = [],
        Relations      = [new RelationDescriptor("Tags", RelationKind.ManyToMany, "Tag", "TagIds")]
    };

    public static SchemaDescriptor TagSchema() => new()
    {
        TypeName       = "Tag",
        TableName      = "tags",
        CollectionName = null,
        KeyColumn      = new ColumnDescriptor("Id",      "uuid", false),
        ScalarColumns  = [new ColumnDescriptor("Label",  "text", false)],
        FkColumns      = [],
        VectorFields   = [],
        ChunkFields    = [],
        Relations      = []
    };

    public static SchemaDescriptor ArticleWithProjectionSchema() => new()
    {
        TypeName       = "Article",
        TableName      = "articles",
        CollectionName = null,
        KeyColumn      = new ColumnDescriptor("Id",          "uuid",        false),
        ScalarColumns  =
        [
            new ColumnDescriptor("Title",       "text",        false),
            new ColumnDescriptor("Category",    "text",        false),
            new ColumnDescriptor("WordCount",   "integer",     false),
            new ColumnDescriptor("PublishedAt", "timestamptz", false),
            new ColumnDescriptor("Body",        "text",        false),
        ],
        FkColumns    = [],
        VectorFields = [],
        ChunkFields  = [],
        Relations    = [],
        SearchKeyColumns  = ["Category", "PublishedAt"],
        LargeFieldColumns = ["Body"]
    };
}
