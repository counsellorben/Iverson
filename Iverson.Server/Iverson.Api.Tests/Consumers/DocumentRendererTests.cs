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
    private readonly SchemaRegistry _registry;
    private readonly DocumentRenderer _sut;

    private const string Tenant = "test-tenant";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static readonly string WidgetId  = "11111111-0000-0000-0000-000000000001";
    private static readonly string AuthorId  = "22222222-0000-0000-0000-000000000002";
    private static readonly string CategoryA = "44444444-0000-0000-0000-0000000000aa";

    public DocumentRendererTests()
    {
        _registry = new SchemaRegistry(new SchemaRegistryRepository(_sql), NullLogger<SchemaRegistry>.Instance);
        _sut = new DocumentRenderer();
    }

    // ── Schema fixture ───────────────────────────────────────────────────────
    // A Widget declares a ManyToOne Author, a ManyToOne Editor, a OneToMany Comments and a
    // ManyToMany Categories. Registration no longer accepts a template that references them, but a
    // descriptor stored before that change can still carry such segments.

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

    // ── Case-insensitive resolution (final-review Finding 1) ────────────────

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

    // ── Legacy relation segments ─────────────────────────────────────────────

    [Fact]
    public async Task RenderAsync_StoredTemplateWithRelationSegments_RendersThemEmptyIncludingBlockLiterals()
    {
        // Loaded from stored JSON exactly as SchemaRegistry.LoadAsync loads it, which does not
        // re-validate templates, so the parsed OneHop and Block segments survive. Each renders as
        // nothing, including a block's own literal text.
        const string template =
            "Name: {Name}|By {Author.Name}|{#Comments}prefix-{Body}{/Comments}|{#Categories}{Label}{/Categories}|end";
        var repository = Substitute.For<ISchemaRegistryRepository>();
        repository.LoadAllAsync().Returns(new List<(string TypeName, string SchemaJson)>
        {
            ("Widget", JsonSerializer.Serialize(
                WidgetSchema(template),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }))
        });
        var stored = new SchemaRegistry(repository, NullLogger<SchemaRegistry>.Instance);
        await stored.LoadAsync();
        var schema = stored.Get("Widget")!;
        schema.DocumentTemplate!.Segments.Select(s => s.Kind).Should()
            .Contain(DocumentSegmentKind.OneHop).And.Contain(DocumentSegmentKind.Block);

        var result = await _sut.RenderAsync(
            schema,
            Payload($$"""{"Id":"{{WidgetId}}","Name":"Sprocket","AuthorId":"{{AuthorId}}","CategoryIds":["{{CategoryA}}"],"TenantId":"{{Tenant}}"}"""),
            Tenant, Ct);

        result.Should().Be("Name: Sprocket|By |||end");
    }
}
