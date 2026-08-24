using Iverson.Api.Schema;

namespace Iverson.Api.Tests.Helpers;

/// <summary>
/// THE FIXTURE-SHAPE INVERSION, recorded rather than fixed (final whole-branch review, minor m1).
/// Every descriptor in this file — and 45 sites across Iverson.Api.Tests — sets
/// <c>TenantColumn = "TenantId"</c>, the CLIENT-DECLARED legacy shape, against 18 sites using
/// <c>SchemaDescriptor.TenantColumnName</c>. So this assembly's DEFAULT descriptor is one
/// <c>SchemaBuilder.BuildDescriptor</c> can no longer produce.
/// <para>
/// This is not wrong in itself — the legacy shape is live and deliberately still admitted by
/// <c>SchemaRegistry.LoadAsync</c>, so exercising it is valuable — but it is WHY two findings hid:
/// Ruling 70 (Iverson.StarRocks keyed its tenant-column exclusion on the per-schema VALUE while
/// Iverson.Api keyed every one of its own on the RESERVED LITERAL, and no fixture that met both
/// sides existed), and Task 7's <c>tenantScoped:</c> mutants M4/M5, which survive because the
/// expression they mutate is already true for every fixture here.
/// </para>
/// <para>
/// DELIBERATELY NOT MASS-REWRITTEN. Flipping 45 sites to the reserved name would silently retire
/// the legacy-shape coverage that Ruling 30 established is load-bearing. The remedy is the opposite:
/// where a rule DEPENDS on which shape it is given, the test must say so and pin BOTH — as
/// <c>SchemaBuilderTests.ToEngagementQuerySchema_LegacyClientDeclaredTenantColumn_*</c> and its
/// reserved-name sibling now do.
/// </para>
/// </summary>
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
        // AuthorId appears in BOTH ScalarColumns and FkColumns, matching what SchemaBuilder
        // really produces (every non-key property becomes a scalar; FK-named ones are
        // *additionally* recorded as FKs).
        ScalarColumns  = [
            new ColumnDescriptor("Title", "text", false),
            new ColumnDescriptor("Body", "text", false),
            new ColumnDescriptor("AuthorId", "uuid", false)],
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
