using System.Text.Json;
using FluentAssertions;
using Iverson.Api.Consumers;
using Iverson.Api.Schema;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Consumers;

public class DocumentRendererTests
{
    private readonly IRecordStoreQueryExecutor _sql = Substitute.For<IRecordStoreQueryExecutor>();
    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();
    private readonly SchemaRegistry _registry;
    private readonly DocumentRenderer _sut;

    private const string Tenant = "test-tenant";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static readonly string WidgetId  = "11111111-0000-0000-0000-000000000001";
    private static readonly string AuthorId  = "22222222-0000-0000-0000-000000000002";
    private static readonly string CommentA  = "33333333-0000-0000-0000-0000000000aa";
    private static readonly string CommentB  = "33333333-0000-0000-0000-0000000000bb";
    private static readonly string CategoryA = "44444444-0000-0000-0000-0000000000aa";
    private static readonly string CategoryB = "44444444-0000-0000-0000-0000000000bb";

    public DocumentRendererTests()
    {
        _registry = new SchemaRegistry(new SchemaRegistryRepository(_sql), NullLogger<SchemaRegistry>.Instance);
        _sut = new DocumentRenderer(_registry, _entities);
    }

    // ── Schema fixtures ──────────────────────────────────────────────────────
    // A Widget has a ManyToOne Author (conventional FK "AuthorId"), a ManyToOne Editor with a
    // deliberately non-conventional FK ("EditorRef"), a OneToMany Comments (FK lives on
    // Comment.WidgetId), and a ManyToMany Categories (FK is Widget.CategoryIds, a uuid[]).

    private static SchemaDescriptor WidgetSchema(string template) => new()
    {
        TypeName      = "Widget",
        TableName     = "widgets",
        KeyColumn     = new ColumnDescriptor("Id", "UUID", false),
        ScalarColumns =
        [
            new ColumnDescriptor("Name",       "TEXT", true),
            new ColumnDescriptor("Rating",     "DOUBLE PRECISION", true),
            new ColumnDescriptor("Count",      "INTEGER", true),
            new ColumnDescriptor("Active",     "BOOLEAN", true),
            new ColumnDescriptor("CreatedAt",  "TIMESTAMPTZ", true),
            new ColumnDescriptor("Tags",       "TEXT[]", true),
            new ColumnDescriptor("Ref",        "UUID", true),
            new ColumnDescriptor("AuthorId",   "UUID", true),
            new ColumnDescriptor("EditorRef",  "UUID", true),
            new ColumnDescriptor("CategoryIds","UUID[]", true),
        ],
        FkColumns    = [],
        VectorFields = [],
        ChunkFields  = [],
        Relations    =
        [
            new RelationDescriptor("Author",     RelationKind.ManyToOne,  "Author",   "AuthorId"),
            new RelationDescriptor("Editor",     RelationKind.ManyToOne,  "Author",   "EditorRef"),
            new RelationDescriptor("Comments",   RelationKind.OneToMany,  "Comment",  "WidgetId"),
            new RelationDescriptor("Categories", RelationKind.ManyToMany,"Category", "CategoryIds"),
        ],
        TenantColumn       = "TenantId",
        DocumentTemplate       = DocumentTemplateParser.Parse(template),
        DocumentTemplateSource = template,
    };

    private static SchemaDescriptor AuthorSchema() => new()
    {
        TypeName      = "Author",
        TableName     = "authors",
        KeyColumn     = new ColumnDescriptor("Id", "UUID", false),
        ScalarColumns = [new ColumnDescriptor("Name", "TEXT", false)],
        FkColumns     = [],
        VectorFields  = [],
        ChunkFields   = [],
        Relations     = [],
        TenantColumn  = "TenantId",
    };

    private static SchemaDescriptor CommentSchema() => new()
    {
        TypeName      = "Comment",
        TableName     = "comments",
        KeyColumn     = new ColumnDescriptor("Id", "UUID", false),
        ScalarColumns =
        [
            new ColumnDescriptor("Body",     "TEXT", false),
            new ColumnDescriptor("WidgetId", "UUID", false),
        ],
        FkColumns    = [],
        VectorFields = [],
        ChunkFields  = [],
        Relations    = [],
        TenantColumn = "TenantId",
    };

    private static SchemaDescriptor CategorySchema() => new()
    {
        TypeName      = "Category",
        TableName     = "categories",
        KeyColumn     = new ColumnDescriptor("Id", "UUID", false),
        ScalarColumns = [new ColumnDescriptor("Label", "TEXT", false)],
        FkColumns     = [],
        VectorFields  = [],
        ChunkFields   = [],
        Relations     = [],
        TenantColumn  = "TenantId",
    };

    private static JsonElement Payload(string json) => JsonDocument.Parse(json).RootElement;

    // ── Scalar + literal + escape ────────────────────────────────────────────

    [Fact]
    public async Task RenderAsync_ScalarPlaceholder_RendersVerbatimStringValue()
    {
        await _registry.RegisterAsync(WidgetSchema("Name is {Name}."));

        var result = await _sut.RenderAsync(
            _registry.Get("Widget")!,
            Payload($$"""{"Id":"{{WidgetId}}","Name":"Sprocket","TenantId":"{{Tenant}}"}"""),
            Tenant, Ct);

        result.Should().Be("Name is Sprocket.");
    }

    [Fact]
    public async Task RenderAsync_EscapedBrace_RendersLiteralBraceNotAsPlaceholder()
    {
        await _registry.RegisterAsync(WidgetSchema("{{Name}} = {Name}"));

        var result = await _sut.RenderAsync(
            _registry.Get("Widget")!,
            Payload($$"""{"Id":"{{WidgetId}}","Name":"Sprocket","TenantId":"{{Tenant}}"}"""),
            Tenant, Ct);

        result.Should().Be("{Name}} = Sprocket");
    }

    [Fact]
    public async Task RenderAsync_MissingOrNullScalar_RendersEmptyString()
    {
        await _registry.RegisterAsync(WidgetSchema("[{Name}]"));

        var result = await _sut.RenderAsync(
            _registry.Get("Widget")!,
            Payload($$"""{"Id":"{{WidgetId}}","Name":null,"TenantId":"{{Tenant}}"}"""),
            Tenant, Ct);

        result.Should().Be("[]");
    }

    // ── Each scalar type's invariant rendering ───────────────────────────────

    [Fact]
    public async Task RenderAsync_AllScalarTypes_RenderCultureInvariantly()
    {
        await _registry.RegisterAsync(
            WidgetSchema("{Name}|{Rating}|{Count}|{Active}|{CreatedAt}|{Ref}|{Tags}"));

        var result = await _sut.RenderAsync(
            _registry.Get("Widget")!,
            Payload($$"""
                {
                  "Id":"{{WidgetId}}",
                  "Name":"Sprocket",
                  "Rating":3.5,
                  "Count":42,
                  "Active":true,
                  "CreatedAt":"2026-08-21T10:15:30Z",
                  "Ref":"22222222-0000-0000-0000-000000000002",
                  "Tags":["a","b","c"],
                  "TenantId":"{{Tenant}}"
                }
                """),
            Tenant, Ct);

        result.Should().Be(
            "Sprocket|3.5|42|true|2026-08-21T10:15:30Z|22222222-0000-0000-0000-000000000002|a, b, c");
    }

    // ── OneHop (single-valued relation) ──────────────────────────────────────

    [Fact]
    public async Task RenderAsync_OneHop_RendersTargetScalarProperty()
    {
        await _registry.RegisterAsync(WidgetSchema("By {Author.Name}"));
        await _registry.RegisterAsync(AuthorSchema());

        _entities.FetchManyByKeysAsync(
                Arg.Is<TableSchema>(s => s.TableName == "authors"),
                Arg.Any<IReadOnlyList<string>>(), true, Tenant)
            .Returns([new KeyedRow(AuthorId, $$"""{"Id":"{{AuthorId}}","Name":"Alice","TenantId":"{{Tenant}}"}""")]);

        var result = await _sut.RenderAsync(
            _registry.Get("Widget")!,
            Payload($$"""{"Id":"{{WidgetId}}","AuthorId":"{{AuthorId}}","TenantId":"{{Tenant}}"}"""),
            Tenant, Ct);

        result.Should().Be("By Alice");
    }

    [Fact]
    public async Task RenderAsync_OneHop_NullForeignKey_RendersEmptyAndDoesNotFetch()
    {
        await _registry.RegisterAsync(WidgetSchema("By {Author.Name}"));
        await _registry.RegisterAsync(AuthorSchema());

        var result = await _sut.RenderAsync(
            _registry.Get("Widget")!,
            Payload($$"""{"Id":"{{WidgetId}}","AuthorId":null,"TenantId":"{{Tenant}}"}"""),
            Tenant, Ct);

        result.Should().Be("By ");
        await _entities.DidNotReceive().FetchManyByKeysAsync(
            Arg.Any<TableSchema>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<bool>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task RenderAsync_OneHop_DeletedTargetRow_RendersEmpty()
    {
        await _registry.RegisterAsync(WidgetSchema("By {Author.Name}"));
        await _registry.RegisterAsync(AuthorSchema());

        // The FK still points at AuthorId, but the row was deleted: the repository returns no
        // match for that key.
        _entities.FetchManyByKeysAsync(
                Arg.Is<TableSchema>(s => s.TableName == "authors"),
                Arg.Any<IReadOnlyList<string>>(), true, Tenant)
            .Returns([]);

        var result = await _sut.RenderAsync(
            _registry.Get("Widget")!,
            Payload($$"""{"Id":"{{WidgetId}}","AuthorId":"{{AuthorId}}","TenantId":"{{Tenant}}"}"""),
            Tenant, Ct);

        result.Should().Be("By ");
    }

    [Fact]
    public async Task RenderAsync_OneHop_ExplicitNonConventionalForeignKey_ReadsDeclaredColumn()
    {
        // Editor's ForeignKey is "EditorRef", not the "EditorId" the naming convention would
        // produce — the renderer must read the declared ForeignKey, not derive one.
        await _registry.RegisterAsync(WidgetSchema("Edited by {Editor.Name}"));
        await _registry.RegisterAsync(AuthorSchema());

        _entities.FetchManyByKeysAsync(
                Arg.Is<TableSchema>(s => s.TableName == "authors"),
                Arg.Is<IReadOnlyList<string>>(k => k.Contains(AuthorId)), true, Tenant)
            .Returns([new KeyedRow(AuthorId, $$"""{"Id":"{{AuthorId}}","Name":"Bob","TenantId":"{{Tenant}}"}""")]);

        var result = await _sut.RenderAsync(
            _registry.Get("Widget")!,
            // No "EditorId" property at all — only the declared "EditorRef".
            Payload($$"""{"Id":"{{WidgetId}}","EditorRef":"{{AuthorId}}","TenantId":"{{Tenant}}"}"""),
            Tenant, Ct);

        result.Should().Be("Edited by Bob");
    }

    // ── Block over OneToMany ─────────────────────────────────────────────────

    [Fact]
    public async Task RenderAsync_BlockOverOneToMany_RendersEachRowConcatenated()
    {
        await _registry.RegisterAsync(WidgetSchema("Comments:{#Comments}[{Body}]{/Comments}"));
        await _registry.RegisterAsync(CommentSchema());

        _entities.FetchByColumnAsync(
                Arg.Is<TableSchema>(s => s.TableName == "comments"), "WidgetId", WidgetId, true, Tenant)
            .Returns([
                $$"""{"Id":"{{CommentA}}","Body":"first","WidgetId":"{{WidgetId}}","TenantId":"{{Tenant}}"}""",
                $$"""{"Id":"{{CommentB}}","Body":"second","WidgetId":"{{WidgetId}}","TenantId":"{{Tenant}}"}"""
            ]);

        var result = await _sut.RenderAsync(
            _registry.Get("Widget")!,
            Payload($$"""{"Id":"{{WidgetId}}","TenantId":"{{Tenant}}"}"""),
            Tenant, Ct);

        // CommentA < CommentB, so this ordering is also confirming the sort (see the dedicated
        // determinism test below for the mutation-testable guard).
        result.Should().Be("Comments:[first][second]");
        await _entities.Received(1).FetchByColumnAsync(
            Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<string>(), tenantScoped: true, tenantId: Tenant);
    }

    [Fact]
    public async Task RenderAsync_BlockOverOneToMany_EmptyCollection_EmitsNothingIncludingBlockLiterals()
    {
        await _registry.RegisterAsync(WidgetSchema("Before|{#Comments}prefix-{Body}{/Comments}|After"));
        await _registry.RegisterAsync(CommentSchema());

        _entities.FetchByColumnAsync(
                Arg.Is<TableSchema>(s => s.TableName == "comments"), "WidgetId", WidgetId, true, Tenant)
            .Returns([]);

        var result = await _sut.RenderAsync(
            _registry.Get("Widget")!,
            Payload($$"""{"Id":"{{WidgetId}}","TenantId":"{{Tenant}}"}"""),
            Tenant, Ct);

        result.Should().Be("Before||After");
    }

    // ── Block over ManyToMany ────────────────────────────────────────────────

    [Fact]
    public async Task RenderAsync_BlockOverManyToMany_RendersEachRowConcatenated()
    {
        await _registry.RegisterAsync(WidgetSchema("Cats:{#Categories}[{Label}]{/Categories}"));
        await _registry.RegisterAsync(CategorySchema());

        _entities.FetchManyByKeysAsync(
                Arg.Is<TableSchema>(s => s.TableName == "categories"),
                Arg.Any<IReadOnlyList<string>>(), true, Tenant)
            .Returns([
                new KeyedRow(CategoryA, $$"""{"Id":"{{CategoryA}}","Label":"red","TenantId":"{{Tenant}}"}"""),
                new KeyedRow(CategoryB, $$"""{"Id":"{{CategoryB}}","Label":"blue","TenantId":"{{Tenant}}"}""")
            ]);

        var result = await _sut.RenderAsync(
            _registry.Get("Widget")!,
            Payload($$"""{"Id":"{{WidgetId}}","CategoryIds":["{{CategoryA}}","{{CategoryB}}"],"TenantId":"{{Tenant}}"}"""),
            Tenant, Ct);

        result.Should().Be("Cats:[red][blue]");
        await _entities.Received(1).FetchManyByKeysAsync(
            Arg.Any<TableSchema>(), Arg.Any<IReadOnlyList<string>>(), tenantScoped: true, tenantId: Tenant);
    }

    // ── Sort determinism (block rows must render identically regardless of fetch order) ────────

    [Fact]
    public async Task RenderAsync_BlockRows_SortedByKeyRegardlessOfFetchOrder()
    {
        await _registry.RegisterAsync(WidgetSchema("{#Comments}{Body}{/Comments}"));
        await _registry.RegisterAsync(CommentSchema());

        var payload = Payload($$"""{"Id":"{{WidgetId}}","TenantId":"{{Tenant}}"}""");

        _entities.FetchByColumnAsync(
                Arg.Is<TableSchema>(s => s.TableName == "comments"), "WidgetId", WidgetId, true, Tenant)
            .Returns([
                $$"""{"Id":"{{CommentB}}","Body":"second","WidgetId":"{{WidgetId}}","TenantId":"{{Tenant}}"}""",
                $$"""{"Id":"{{CommentA}}","Body":"first","WidgetId":"{{WidgetId}}","TenantId":"{{Tenant}}"}"""
            ]);
        var firstOrderResult = await _sut.RenderAsync(_registry.Get("Widget")!, payload, Tenant, Ct);

        _entities.FetchByColumnAsync(
                Arg.Is<TableSchema>(s => s.TableName == "comments"), "WidgetId", WidgetId, true, Tenant)
            .Returns([
                $$"""{"Id":"{{CommentA}}","Body":"first","WidgetId":"{{WidgetId}}","TenantId":"{{Tenant}}"}""",
                $$"""{"Id":"{{CommentB}}","Body":"second","WidgetId":"{{WidgetId}}","TenantId":"{{Tenant}}"}"""
            ]);
        var secondOrderResult = await _sut.RenderAsync(_registry.Get("Widget")!, payload, Tenant, Ct);

        firstOrderResult.Should().Be("firstsecond");
        secondOrderResult.Should().Be("firstsecond");
        firstOrderResult.Should().Be(secondOrderResult);
    }

    [Fact]
    public async Task RenderAsync_ManyToManyBlockRows_SortedByKeyRegardlessOfFetchOrder()
    {
        await _registry.RegisterAsync(WidgetSchema("{#Categories}{Label}{/Categories}"));
        await _registry.RegisterAsync(CategorySchema());

        var payload = Payload(
            $$"""{"Id":"{{WidgetId}}","CategoryIds":["{{CategoryA}}","{{CategoryB}}"],"TenantId":"{{Tenant}}"}""");

        _entities.FetchManyByKeysAsync(
                Arg.Is<TableSchema>(s => s.TableName == "categories"),
                Arg.Any<IReadOnlyList<string>>(), true, Tenant)
            .Returns([
                new KeyedRow(CategoryB, $$"""{"Id":"{{CategoryB}}","Label":"blue","TenantId":"{{Tenant}}"}"""),
                new KeyedRow(CategoryA, $$"""{"Id":"{{CategoryA}}","Label":"red","TenantId":"{{Tenant}}"}""")
            ]);
        var firstOrderResult = await _sut.RenderAsync(_registry.Get("Widget")!, payload, Tenant, Ct);

        _entities.FetchManyByKeysAsync(
                Arg.Is<TableSchema>(s => s.TableName == "categories"),
                Arg.Any<IReadOnlyList<string>>(), true, Tenant)
            .Returns([
                new KeyedRow(CategoryA, $$"""{"Id":"{{CategoryA}}","Label":"red","TenantId":"{{Tenant}}"}"""),
                new KeyedRow(CategoryB, $$"""{"Id":"{{CategoryB}}","Label":"blue","TenantId":"{{Tenant}}"}""")
            ]);
        var secondOrderResult = await _sut.RenderAsync(_registry.Get("Widget")!, payload, Tenant, Ct);

        firstOrderResult.Should().Be("redblue");
        secondOrderResult.Should().Be("redblue");
        firstOrderResult.Should().Be(secondOrderResult);
    }

    // ── Batching: N placeholders on one relation cost exactly one fetch ─────────

    [Fact]
    public async Task RenderAsync_ThreePlaceholdersOnSameRelation_IssuesExactlyOneFetch()
    {
        await _registry.RegisterAsync(WidgetSchema("{Author.Name}-{Author.Name}-{Author.Name}"));
        await _registry.RegisterAsync(AuthorSchema());

        _entities.FetchManyByKeysAsync(
                Arg.Is<TableSchema>(s => s.TableName == "authors"),
                Arg.Any<IReadOnlyList<string>>(), true, Tenant)
            .Returns([new KeyedRow(AuthorId, $$"""{"Id":"{{AuthorId}}","Name":"Alice","TenantId":"{{Tenant}}"}""")]);

        var result = await _sut.RenderAsync(
            _registry.Get("Widget")!,
            Payload($$"""{"Id":"{{WidgetId}}","AuthorId":"{{AuthorId}}","TenantId":"{{Tenant}}"}"""),
            Tenant, Ct);

        result.Should().Be("Alice-Alice-Alice");
        await _entities.Received(1).FetchManyByKeysAsync(
            Arg.Any<TableSchema>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<bool>(), Arg.Any<string?>());
    }

    // ── Tenant scoping ───────────────────────────────────────────────────────

    [Fact]
    public async Task RenderAsync_OneHop_PassesTenantScopedTrueAndAuthoritativeTenantId()
    {
        await _registry.RegisterAsync(WidgetSchema("By {Author.Name}"));
        await _registry.RegisterAsync(AuthorSchema());

        _entities.FetchManyByKeysAsync(
                Arg.Any<TableSchema>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns([]);

        await _sut.RenderAsync(
            _registry.Get("Widget")!,
            Payload($$"""{"Id":"{{WidgetId}}","AuthorId":"{{AuthorId}}","TenantId":"{{Tenant}}"}"""),
            Tenant, Ct);

        await _entities.Received(1).FetchManyByKeysAsync(
            Arg.Any<TableSchema>(), Arg.Any<IReadOnlyList<string>>(), tenantScoped: true, tenantId: Tenant);
    }

    [Fact]
    public async Task RenderAsync_OneHop_RowInAnotherTenant_DoesNotRender()
    {
        // A tenant-scoped repository never returns a row belonging to another tenant. The
        // substitute models that directly: it only answers for the exact tenantId the renderer
        // must pass through, and returns nothing for any other value.
        await _registry.RegisterAsync(WidgetSchema("By {Author.Name}"));
        await _registry.RegisterAsync(AuthorSchema());

        _entities.FetchManyByKeysAsync(
                Arg.Is<TableSchema>(s => s.TableName == "authors"),
                Arg.Any<IReadOnlyList<string>>(), true, "other-tenant")
            .Returns([new KeyedRow(AuthorId, $$"""{"Id":"{{AuthorId}}","Name":"Alice","TenantId":"other-tenant"}""")]);
        _entities.FetchManyByKeysAsync(
                Arg.Is<TableSchema>(s => s.TableName == "authors"),
                Arg.Any<IReadOnlyList<string>>(), true, Tenant)
            .Returns([]);

        var result = await _sut.RenderAsync(
            _registry.Get("Widget")!,
            Payload($$"""{"Id":"{{WidgetId}}","AuthorId":"{{AuthorId}}","TenantId":"{{Tenant}}"}"""),
            Tenant, Ct);

        result.Should().Be("By ");
    }

    // ── Case-insensitive resolution (final-review Finding 1) ────────────────
    // Registration validation matches relation/property names case-insensitively; the renderer
    // must resolve the same way or a legacy/bypassed schema throws out of ingest instead of
    // rendering the way it validated.

    [Fact]
    public async Task RenderAsync_OneHop_RelationNameCaseMismatch_ResolvesRelationCaseInsensitively()
    {
        // Template spells the relation "author" (lowercase); the declared relation is "Author".
        await _registry.RegisterAsync(WidgetSchema("By {author.Name}"));
        await _registry.RegisterAsync(AuthorSchema());

        _entities.FetchManyByKeysAsync(
                Arg.Is<TableSchema>(s => s.TableName == "authors"),
                Arg.Any<IReadOnlyList<string>>(), true, Tenant)
            .Returns([new KeyedRow(AuthorId, $$"""{"Id":"{{AuthorId}}","Name":"Alice","TenantId":"{{Tenant}}"}""")]);

        var result = await _sut.RenderAsync(
            _registry.Get("Widget")!,
            Payload($$"""{"Id":"{{WidgetId}}","AuthorId":"{{AuthorId}}","TenantId":"{{Tenant}}"}"""),
            Tenant, Ct);

        result.Should().Be("By Alice");
    }

    [Fact]
    public async Task RenderAsync_Block_RelationNameCaseMismatch_ResolvesRelationCaseInsensitively()
    {
        // Template spells the relation "comments" (lowercase); the declared relation is "Comments".
        await _registry.RegisterAsync(WidgetSchema("{#comments}{Body}{/comments}"));
        await _registry.RegisterAsync(CommentSchema());

        _entities.FetchByColumnAsync(
                Arg.Is<TableSchema>(s => s.TableName == "comments"), "WidgetId", WidgetId, true, Tenant)
            .Returns([$$"""{"Id":"{{CommentA}}","Body":"first","WidgetId":"{{WidgetId}}","TenantId":"{{Tenant}}"}"""]);

        var result = await _sut.RenderAsync(
            _registry.Get("Widget")!,
            Payload($$"""{"Id":"{{WidgetId}}","TenantId":"{{Tenant}}"}"""),
            Tenant, Ct);

        result.Should().Be("first");
    }

    [Fact]
    public async Task RenderAsync_Scalar_PropertyNameCaseMismatch_ResolvesCaseInsensitively()
    {
        // Template spells the scalar "{NAME}" in a case matching neither the declared column
        // ("Name") nor its camelCase form ("name").
        await _registry.RegisterAsync(WidgetSchema("Value: {NAME}"));

        var result = await _sut.RenderAsync(
            _registry.Get("Widget")!,
            Payload($$"""{"Id":"{{WidgetId}}","Name":"Widget One","TenantId":"{{Tenant}}"}"""),
            Tenant, Ct);

        result.Should().Be("Value: Widget One");
    }
}
