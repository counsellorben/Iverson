using Iverson.Api.Schema;

namespace Iverson.Api.Tests.Helpers;

public static class SchemaFixtures
{
    // Permissive bypass: existing tests don't configure Authorization, so every fixture
    // grants "test-bypass" full read/write/delete access, short-circuiting ownership and
    // field-level checks once enforcement is wired into the RPC methods (Tasks 2-6).
    private static AuthorizationRules BypassAuthorization() =>
        new(null, new List<RowPermission> { new("test-bypass", true, true, true) }, new List<FieldPermission>());

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
        Relations      = [],
        Authorization  = BypassAuthorization(),
        TenantColumn   = "TenantId"
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
        Relations      = [new RelationDescriptor("Author", RelationKind.ManyToOne, "Author", "AuthorId")],
        Authorization  = BypassAuthorization(),
        TenantColumn   = "TenantId"
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
        ],
        Authorization  = BypassAuthorization(),
        TenantColumn   = "TenantId"
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
        ],
        Authorization  = BypassAuthorization(),
        TenantColumn   = "TenantId"
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
        Relations      = [new RelationDescriptor("Tags", RelationKind.ManyToMany, "Tag", "TagIds")],
        Authorization  = BypassAuthorization(),
        TenantColumn   = "TenantId"
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
        Relations      = [],
        Authorization  = BypassAuthorization(),
        TenantColumn   = "TenantId"
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
        LargeFieldColumns = ["Body"],
        Authorization = BypassAuthorization(),
        TenantColumn  = "TenantId"
    };
}
