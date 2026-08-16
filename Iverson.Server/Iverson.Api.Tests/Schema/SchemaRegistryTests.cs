using FluentAssertions;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Sql;
using Microsoft.Extensions.Logging;
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
    public async Task LoadAsync_DescriptorWithNavPropertyForeignKeyCollision_LogsErrorAndStillLoads()
    {
        // SchemaRegistry.LoadAsync rehydrates descriptors straight from Postgres JSON and does
        // NOT route them through SchemaRegistrationOrchestrator, so a schema persisted before the
        // registration-time collision check existed (PropertyName == ForeignKey on a relation)
        // can still be sitting there. Startup must NOT fail on it — that would take down a
        // running deployment on a legacy schema — but it must be flagged loudly so it gets
        // re-registered, since every Create/Update against it fails RelationValidator anyway.
        var logger = Substitute.For<ILogger<SchemaRegistry>>();
        var sut = new SchemaRegistry(_repository, logger);

        var collidingSchema = SchemaFixtures.ArticleSchema() with
        {
            Relations = [new RelationDescriptor("AuthorId", RelationKind.ManyToOne, "Author", "AuthorId")]
        };

        _repository.LoadAllAsync()
            .Returns(new List<(string TypeName, string SchemaJson)>
            {
                ("Article", System.Text.Json.JsonSerializer.Serialize(
                    collidingSchema,
                    new System.Text.Json.JsonSerializerOptions
                        { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }))
            });

        var act = () => sut.LoadAsync();

        await act.Should().NotThrowAsync();

        sut.IsRegistered("Article").Should().BeTrue();
        sut.Get("Article")!.Relations.Single().PropertyName.Should().Be("AuthorId");

        logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Article") && o.ToString()!.Contains("AuthorId")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}
