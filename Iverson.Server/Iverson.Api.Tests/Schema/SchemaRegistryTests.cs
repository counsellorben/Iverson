using FluentAssertions;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Sql;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Schema;

public class SchemaRegistryTests
{
    private readonly ISchemaRegistryRepository _repository;
    private readonly SchemaRegistry _sut;

    // A recording logger rather than NullLogger: LoadAsync's two rejection arms (the JsonException
    // catch and the IsNullOrEmpty skip) produce the SAME observable outcome — the type is not
    // registered — so IsRegistered alone cannot tell them apart, and a test asserting only that
    // would pass no matter which arm ran, or if a future third arm ran instead. The log message is
    // the only thing that discriminates them, so the boundary tests below assert on it.
    private readonly RecordingLogger<SchemaRegistry> _logs = new();

    public SchemaRegistryTests()
    {
        _repository = Substitute.For<ISchemaRegistryRepository>();
        _sut = new SchemaRegistry(_repository, _logs);
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

    // ── The deserialization boundary (Task 7) ────────────────────────────────
    //
    // SchemaDescriptor.TenantColumn is non-nullable and `required`, and roughly a dozen runtime
    // null guards were deleted downstream on the strength of that. Neither annotation is a
    // runtime guarantee across System.Text.Json — these three facts are what make it one, and
    // they are the only thing standing between an upgraded deployment carrying a pre-2026-07-17
    // _iverson_schema row (written before 63a577a, when the column did not exist) and a
    // NullReferenceException on its first read, write or projection.

    /// <summary>
    /// A legacy row whose JSON has no <c>tenantColumn</c> key at all. `required` makes
    /// System.Text.Json throw ("missing required properties including: 'tenantColumn'"); LoadAsync
    /// must contain that to the one row rather than letting it abort the whole load, because the
    /// same method runs on SchemaRefreshWorker's 30 s poll and a throw there freezes every other
    /// schema's refresh too.
    /// </summary>
    [Fact]
    public async Task LoadAsync_LegacyRowWithNoTenantColumnKey_IsSkipped_AndDoesNotAbortTheLoad()
    {
        var goodJson = SerializeAsRegistryWould(SchemaFixtures.AuthorSchema());
        var legacyJson = SerializeAsRegistryWould(SchemaFixtures.ArticleSchema())
            .Replace(",\"tenantColumn\":\"TenantId\"", "", StringComparison.Ordinal);
        legacyJson.Should().NotContain("tenantColumn", "the fixture must actually reproduce a pre-cutover row");

        _repository.LoadAllAsync().Returns(new List<(string TypeName, string SchemaJson)>
        {
            ("Article", legacyJson),
            ("Author",  goodJson)
        });

        await _sut.LoadAsync();

        _sut.IsRegistered("Article").Should().BeFalse("a row with no tenant column must never be admitted");
        _sut.IsRegistered("Author").Should().BeTrue("one bad row must not take the rest of the registry down");

        // Which arm rejected it, not merely that something did: this row must fail inside
        // System.Text.Json (the `required` member is missing) and be caught, NOT reach the
        // IsNullOrEmpty skip, which would mean `required` had stopped doing anything.
        _logs.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Error &&
            e.Message.Contains("could not be deserialized") &&
            e.Message.Contains("Article"),
            "the JsonException catch is the arm that must fire for a row missing the key entirely");
        _logs.Entries.Should().NotContain(e => e.Message.Contains("carries no server-owned tenant column"));
    }

    /// <summary>
    /// The half `required` does NOT cover: a row that HAS the key with an explicit null. Measured
    /// behaviour of System.Text.Json is that this deserializes to null on a `required`
    /// non-nullable member — `required` checks presence, never non-nullness — so an explicit
    /// runtime check is mandatory and this test is what pins it.
    /// </summary>
    [Fact]
    public async Task LoadAsync_RowWithExplicitlyNullTenantColumn_IsSkipped()
    {
        var nulledJson = SerializeAsRegistryWould(SchemaFixtures.ArticleSchema())
            .Replace("\"tenantColumn\":\"TenantId\"", "\"tenantColumn\":null", StringComparison.Ordinal);
        nulledJson.Should().Contain("\"tenantColumn\":null");

        _repository.LoadAllAsync().Returns(new List<(string TypeName, string SchemaJson)>
        {
            ("Article", nulledJson)
        });

        await _sut.LoadAsync();

        _sut.IsRegistered("Article").Should().BeFalse();

        // Which arm rejected it. `required` is satisfied here — the key IS present — so this row
        // must deserialize cleanly and be stopped by the explicit IsNullOrEmpty check. If it were
        // the JsonException catch firing instead, the runtime check this test exists to pin would
        // be unexercised and the assertion above would still pass.
        _logs.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Error &&
            e.Message.Contains("carries no server-owned tenant column") &&
            e.Message.Contains("Article"),
            "the IsNullOrEmpty skip is the arm that must fire for an explicit null");
        _logs.Entries.Should().NotContain(e => e.Message.Contains("could not be deserialized"));
    }

    /// <summary>
    /// The positive control for both tests above: without it, a LoadAsync that admitted NOTHING
    /// would satisfy them.
    /// </summary>
    [Fact]
    public async Task LoadAsync_RowCarryingATenantColumn_IsAdmitted()
    {
        _repository.LoadAllAsync().Returns(new List<(string TypeName, string SchemaJson)>
        {
            ("Article", SerializeAsRegistryWould(SchemaFixtures.ArticleSchema()))
        });

        await _sut.LoadAsync();

        _sut.IsRegistered("Article").Should().BeTrue();
        _sut.Get("Article")!.TenantColumn.Should().Be("TenantId");
        _logs.Entries.Should().NotContain(e => e.Level == LogLevel.Error);
    }

    // Serializes exactly as SchemaRegistry.RegisterAsync does, so the fixtures above are real
    // _iverson_schema rows minus/with the one key under test rather than hand-written JSON that
    // could drift from the shape LoadAsync actually meets.
    private static string SerializeAsRegistryWould(SchemaDescriptor descriptor) =>
        System.Text.Json.JsonSerializer.Serialize(
            descriptor,
            new System.Text.Json.JsonSerializerOptions
                { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

    // Records level + formatted message so a test can assert WHICH log fired, not merely that the
    // registry ended up in some state. Same shape as DocumentRerenderQueueWorkerTests'.
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
