using FluentAssertions;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Schema;

public class SchemaRegistryTests
{
    private readonly ISchemaRegistryRepository _repository;
    private readonly SchemaRegistry _sut;

    public SchemaRegistryTests()
    {
        _repository = Substitute.For<ISchemaRegistryRepository>();
        _sut = new SchemaRegistry(_repository, NullLogger<SchemaRegistry>.Instance);
    }

    [Fact]
    public void Get_ReturnsNull_WhenNotRegistered()
    {
        var result = _sut.Get("NonExistent");

        result.Should().BeNull();
    }

    [Fact]
    public async Task RegisterAsync_StoresDescriptor()
    {
        var schema = SchemaFixtures.AuthorSchema();

        await _sut.RegisterAsync(schema);

        _sut.Get("Author").Should().NotBeNull();
        _sut.Get("Author")!.TypeName.Should().Be("Author");
    }

    [Fact]
    public void IsRegistered_ReturnsFalse_BeforeRegistration()
    {
        _sut.IsRegistered("Author").Should().BeFalse();
    }

    [Fact]
    public async Task IsRegistered_ReturnsTrue_AfterRegistration()
    {
        await _sut.RegisterAsync(SchemaFixtures.AuthorSchema());

        _sut.IsRegistered("Author").Should().BeTrue();
    }

    [Fact]
    public async Task UnregisterAsync_RemovesSchema()
    {
        await _sut.RegisterAsync(SchemaFixtures.AuthorSchema());

        await _sut.UnregisterAsync("Author");

        _sut.IsRegistered("Author").Should().BeFalse();
        _sut.Get("Author").Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_RemovesSchemasNoLongerInPostgres()
    {
        await _sut.RegisterAsync(SchemaFixtures.AuthorSchema());
        await _sut.RegisterAsync(SchemaFixtures.ArticleSchema());
        _sut.IsRegistered("Author").Should().BeTrue();
        _sut.IsRegistered("Article").Should().BeTrue();

        // Simulate "Article" having been unregistered by a different process: the next
        // LoadAsync's query only returns "Author" (matching what UnregisterAsync's DELETE
        // would leave behind in Postgres), even though this instance's in-memory copy still
        // has "Article" from the RegisterAsync call above.
        _repository.LoadAllAsync()
            .Returns(new List<(string TypeName, string SchemaJson)>
            {
                ("Author", System.Text.Json.JsonSerializer.Serialize(
                    SchemaFixtures.AuthorSchema(),
                    new System.Text.Json.JsonSerializerOptions
                        { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }))
            });

        await _sut.LoadAsync();

        _sut.IsRegistered("Author").Should().BeTrue();
        _sut.IsRegistered("Article").Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_RebuildsReverseIndex_SoStartupRehydratedTemplateDependentsAreFound()
    {
        // A templated schema rehydrated from Postgres at process startup (LoadAsync), not via
        // a live RegisterAsync call — proves the reverse index is rebuilt at BOTH mutation
        // points, not just RegisterAsync's. Without the LoadAsync-side rebuild, a fresh
        // process would silently miss every dependent for every templated type until
        // something re-registers.
        var widget = new SchemaDescriptor
        {
            TypeName      = "Widget",
            TableName     = "widgets",
            KeyColumn     = new ColumnDescriptor("Id", "UUID", false),
            ScalarColumns = [new ColumnDescriptor("AuthorId", "UUID", true)],
            FkColumns     = [],
            VectorFields  = [],
            ChunkFields   = [],
            Relations     = [new RelationDescriptor("Author", RelationKind.ManyToOne, "Author", "AuthorId")],
            TenantColumn  = "TenantId",
            DocumentTemplate       = DocumentTemplateParser.Parse("{Author.Name}"),
            DocumentTemplateSource = "{Author.Name}",
        };

        _repository.LoadAllAsync()
            .Returns(new List<(string TypeName, string SchemaJson)>
            {
                ("Widget", System.Text.Json.JsonSerializer.Serialize(
                    widget,
                    new System.Text.Json.JsonSerializerOptions
                        { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }))
            });

        await _sut.LoadAsync();

        var dependents = _sut.GetDependents("Author");
        dependents.Should().ContainSingle(d => d.DeclaringType == "Widget" && d.Relation.PropertyName == "Author");
    }

    [Fact]
    public async Task LoadAsync_MetadataColumns_StayCaseInsensitive_AfterRoundTrip()
    {
        // System.Text.Json rebuilds HashSet<string> with the default case-SENSITIVE comparer,
        // so SchemaDescriptor re-applies OrdinalIgnoreCase in its init accessor. Without that,
        // a Contains("category") lookup would succeed only in the registering process.
        var schema = SchemaFixtures.AuthorSchema() with { MetadataColumns = ["Category"] };
        schema.MetadataColumns.Contains("category").Should().BeTrue();

        _repository.LoadAllAsync()
            .Returns(new List<(string TypeName, string SchemaJson)>
            {
                ("Author", System.Text.Json.JsonSerializer.Serialize(
                    schema,
                    new System.Text.Json.JsonSerializerOptions
                        { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }))
            });

        await _sut.LoadAsync();

        var loaded = _sut.Get("Author");
        loaded.Should().NotBeNull();
        loaded!.MetadataColumns.Contains("category").Should().BeTrue();
        loaded.MetadataColumns.Contains("Category").Should().BeTrue();
    }

    [Fact]
    public async Task LoadAsync_LegacyJsonWithoutMetadataMembers_DeserializesToDefaults()
    {
        // Rows written before the metadata layer existed have no metadataColumns/
        // fieldDescriptions/description keys — deserialization must not throw and must
        // yield the defaulted (empty/null) members.
        const string legacyJson = """
            {
              "typeName": "Author",
              "tableName": "authors",
              "collectionName": null,
              "keyColumn": { "name": "Id", "sqlType": "UUID", "isNullable": false },
              "scalarColumns": [],
              "fkColumns": [],
              "vectorFields": [],
              "chunkFields": [],
              "relations": [],
              "searchKeyColumns": [],
              "largeFieldColumns": [],
              "authorization": null,
              "tenantColumn": "TenantId"
            }
            """;

        _repository.LoadAllAsync()
            .Returns(new List<(string TypeName, string SchemaJson)> { ("Author", legacyJson) });

        await _sut.LoadAsync();

        var descriptor = _sut.Get("Author");
        descriptor.Should().NotBeNull();
        descriptor!.MetadataColumns.Should().BeEmpty();
        descriptor.FieldDescriptions.Should().BeEmpty();
        descriptor.Description.Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_LegacyJsonWithoutDocumentTemplateMembers_DeserializesToDefaults()
    {
        // Rows written before the document-template feature existed have no documentTemplate/
        // documentTemplateSource keys — deserialization must not throw and must yield null.
        const string legacyJson = """
            {
              "typeName": "Author",
              "tableName": "authors",
              "collectionName": null,
              "keyColumn": { "name": "Id", "sqlType": "UUID", "isNullable": false },
              "scalarColumns": [],
              "fkColumns": [],
              "vectorFields": [],
              "chunkFields": [],
              "relations": [],
              "searchKeyColumns": [],
              "largeFieldColumns": [],
              "authorization": null,
              "tenantColumn": "TenantId"
            }
            """;

        _repository.LoadAllAsync()
            .Returns(new List<(string TypeName, string SchemaJson)> { ("Author", legacyJson) });

        await _sut.LoadAsync();

        var descriptor = _sut.Get("Author");
        descriptor.Should().NotBeNull();
        descriptor!.DocumentTemplate.Should().BeNull();
        descriptor.DocumentTemplateSource.Should().BeNull();
    }

    [Fact]
    public async Task RegisterAsync_ThenLoadAsync_RoundTripsDocumentTemplateWithAllSegmentKinds()
    {
        // A block's Inner list is the same DocumentSegment record type reused, so this exercises
        // Literal, Scalar, OneHop, and Block (with nested Literal/Scalar inner segments) in one
        // pass through SchemaRegistry's JsonSerializerOptions (no polymorphic resolver, no
        // converters configured).
        const string source = "{Title} by {Author.Name}{#Tags}- {Name}\n{/Tags}";
        var parsed = DocumentTemplateParser.Parse(source);
        var schema = SchemaFixtures.AuthorSchema() with
        {
            DocumentTemplate       = parsed,
            DocumentTemplateSource = source
        };

        string? upsertedJson = null;
        _ = _repository.UpsertAsync(Arg.Any<string>(), Arg.Do<string>(json => upsertedJson = json));
        await _sut.RegisterAsync(schema);

        _repository.LoadAllAsync()
            .Returns(new List<(string TypeName, string SchemaJson)> { ("Author", upsertedJson!) });

        await _sut.LoadAsync();

        var loaded = _sut.Get("Author");
        loaded.Should().NotBeNull();
        loaded!.DocumentTemplateSource.Should().Be(source);
        loaded.DocumentTemplate.Should().BeEquivalentTo(parsed);
    }
}
