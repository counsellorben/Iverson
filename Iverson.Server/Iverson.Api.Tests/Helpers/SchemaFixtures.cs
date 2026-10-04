using Iverson.Api.Schema;

namespace Iverson.Api.Tests.Helpers;

/// <summary>
/// THE FIXTURE-SHAPE INVERSION, recorded rather than fixed (final whole-branch review, minor m1).
/// Every descriptor in this file except the reserved-tenant Dossier fixtures at the end — and 45
/// sites across Iverson.Api.Tests — sets <c>TenantColumn = "TenantId"</c>, the CLIENT-DECLARED
/// legacy shape, against 18 sites using <c>SchemaDescriptor.TenantColumnName</c>. So this
/// assembly's DEFAULT descriptor is one <c>SchemaBuilder.BuildDescriptor</c> can no longer produce.
/// <para>
/// This is not wrong in itself — the legacy shape is live and deliberately still admitted by
/// <c>SchemaRegistry.LoadAsync</c>, so exercising it is valuable — but it is WHY two findings hid:
/// Ruling 70 (Iverson.StarRocks keyed its tenant-column exclusion on the per-schema VALUE while
/// Iverson.Api keyed every one of its own on the RESERVED LITERAL, and no fixture that met both
/// sides existed), and Task 7's <c>tenantScoped:</c> mutants M4/M5 (that boolean parameter has
/// since been replaced by <c>EntityAccess</c> — see CSR round-3 finding #5 — but the same
/// fixture-shape gap applies to whichever expression now distinguishes tenant-scoped from
/// cross-tenant access), which survive because that expression is already true for every fixture
/// here.
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

    // Employee: ManyToOne(Manager) -> Employee itself. Self-referencing type used to build a
    // genuine cyclic relation graph (A -> B -> A) for EntityRelationResolverTests' CSR finding #6
    // cycle-guard coverage — the finding notes this shape is legal in the schema model.
    public static SchemaDescriptor EmployeeSchema() => new()
    {
        TypeName       = "Employee",
        TableName      = "employees",
        CollectionName = null,
        KeyColumn      = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns  = [new ColumnDescriptor("Name", "text", false), new ColumnDescriptor("ManagerId", "uuid", true)],
        FkColumns      = [new ForeignKeyDescriptor("ManagerId", "Employee")],
        VectorFields   = [],
        ChunkFields    = [],
        Relations      = [new RelationDescriptor("Manager", RelationKind.ManyToOne, "Employee", "ManagerId")],
        Authorization  = BypassAuthorization(),
        TenantColumn   = "TenantId"
    };

    // Team / User(->Team) / Document(->User x2): a DIAMOND relation shape — Document.CreatedBy and
    // Document.UpdatedBy are two DIFFERENT relations that can both point at the SAME User row.
    // Not a cycle (User -> Team is a dead end), but a real regression risk for a traversal-global
    // "seen anywhere in this request" cycle guard, which would silently fail to expand
    // User.Team on the second occurrence even though it's well within MaxRelationDepth. Used by
    // EntityRelationResolverTests' diamond-reference coverage (CSR finding #6 fix-round 1).
    public static SchemaDescriptor DiamondTeamSchema() => new()
    {
        TypeName       = "DiamondTeam",
        TableName      = "diamond_teams",
        CollectionName = null,
        KeyColumn      = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns  = [new ColumnDescriptor("Name", "text", false)],
        FkColumns      = [],
        VectorFields   = [],
        ChunkFields    = [],
        Relations      = [],
        Authorization  = BypassAuthorization(),
        TenantColumn   = "TenantId"
    };

    public static SchemaDescriptor DiamondUserSchema() => new()
    {
        TypeName       = "DiamondUser",
        TableName      = "diamond_users",
        CollectionName = null,
        KeyColumn      = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns  = [new ColumnDescriptor("Name", "text", false), new ColumnDescriptor("TeamId", "uuid", true)],
        FkColumns      = [new ForeignKeyDescriptor("TeamId", "DiamondTeam")],
        VectorFields   = [],
        ChunkFields    = [],
        Relations      = [new RelationDescriptor("Team", RelationKind.ManyToOne, "DiamondTeam", "TeamId")],
        Authorization  = BypassAuthorization(),
        TenantColumn   = "TenantId"
    };

    public static SchemaDescriptor DiamondDocumentSchema() => new()
    {
        TypeName       = "DiamondDocument",
        TableName      = "diamond_documents",
        CollectionName = null,
        KeyColumn      = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns  =
        [
            new ColumnDescriptor("Title", "text", false),
            new ColumnDescriptor("CreatedById", "uuid", false),
            new ColumnDescriptor("UpdatedById", "uuid", false)
        ],
        FkColumns      =
        [
            new ForeignKeyDescriptor("CreatedById", "DiamondUser"),
            new ForeignKeyDescriptor("UpdatedById", "DiamondUser")
        ],
        VectorFields   = [],
        ChunkFields    = [],
        Relations      =
        [
            new RelationDescriptor("CreatedBy", RelationKind.ManyToOne, "DiamondUser", "CreatedById"),
            new RelationDescriptor("UpdatedBy", RelationKind.ManyToOne, "DiamondUser", "UpdatedById")
        ],
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

    // ── Reserved-tenant fixtures ──────────────────────────────────────────────
    //
    // Everything above uses the legacy client-declared TenantColumn = "TenantId". These use the
    // reserved SchemaDescriptor.TenantColumnName, the shape SchemaBuilder.BuildDescriptor produces,
    // because the field-restricted write path depends on which shape it gets:
    // RowFieldAuthorizationEvaluator never lists the reserved column in AllowedFields.
    //
    // Dossier's field permissions are the same in every variant:
    //   Secret, SealedAt, Seal: anyone may read, only "premium" may write;
    //   Notes:                  anyone may write, only "premium" may read.
    // The default acting user (ActingUserFixtures.Principal("test-user", "test-bypass")) holds
    // neither "premium" grant, so it is field-restricted on both actions.

    // "test-bypass" may read and write every row. No OwnerField.
    public static SchemaDescriptor ReservedTenantDossierSchema() =>
        DossierSchema(ownerField: null, [new RowPermission("test-bypass", true, true, true)]);

    // No row permission, so every caller is ownership-scoped through OwnerId.
    public static SchemaDescriptor ReservedTenantOwnedDossierSchema() =>
        DossierSchema(ownerField: "OwnerId", []);

    // "test-bypass" may write every row but has no CanReadAll: CanWriteAll without CanReadAll.
    // ownerFieldName lets a test register the OwnerField spelled unlike its column ("ownerId"),
    // which registration accepts because it matches columns case-insensitively.
    public static SchemaDescriptor ReservedTenantWriteOnlyDossierSchema(bool withOwnerField, string ownerFieldName = "OwnerId") =>
        DossierSchema(withOwnerField ? ownerFieldName : null, [new RowPermission("test-bypass", false, true, false)]);

    private static SchemaDescriptor DossierSchema(string? ownerField, List<RowPermission> rowPermissions) => new()
    {
        TypeName       = "Dossier",
        TableName      = "dossiers",
        CollectionName = null,
        KeyColumn      = new ColumnDescriptor("Id", "UUID", false),
        ScalarColumns  =
        [
            new ColumnDescriptor("Title",    "TEXT",        true),
            new ColumnDescriptor("Secret",   "TEXT",        true),
            new ColumnDescriptor("SealedAt", "TIMESTAMPTZ", true),
            new ColumnDescriptor("Seal",     "BYTEA",       true),
            new ColumnDescriptor("Notes",    "TEXT",        true),
            new ColumnDescriptor("OwnerId",  "TEXT",        true),
            new ColumnDescriptor(SchemaDescriptor.TenantColumnName, "TEXT", false),
        ],
        FkColumns      = [],
        VectorFields   = [],
        ChunkFields    = [],
        Relations      = [],
        Authorization  = new AuthorizationRules(
            ownerField,
            rowPermissions,
            new List<FieldPermission>
            {
                new("Secret",   new List<string>(), new List<string> { "premium" }),
                new("SealedAt", new List<string>(), new List<string> { "premium" }),
                new("Seal",     new List<string>(), new List<string> { "premium" }),
                new("Notes",    new List<string> { "premium" }, new List<string>()),
            }),
        TenantColumn   = SchemaDescriptor.TenantColumnName
    };
}
