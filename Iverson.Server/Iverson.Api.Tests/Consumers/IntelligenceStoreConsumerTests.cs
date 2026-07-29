using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Iverson.Api.Consumers;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Embeddings;
using Iverson.Events;
using Iverson.Sql;
using Iverson.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Qdrant.Client.Grpc;
using Xunit;

namespace Iverson.Api.Tests.Consumers;

public class IntelligenceStoreConsumerTests
{
    private readonly IEventConsumer _consumer;
    private readonly IVectorSchemaManager _vectorSchema;
    private readonly IVectorWriteService _vectorWrite;
    private readonly IEmbeddingService _embedding;
    private readonly IRecordStoreQueryExecutor _sql;
    private readonly IEntityRepository _entities;
    private readonly SchemaRegistry _registry;
    private readonly IEnrichmentService _enrichment;
    private readonly EnrichmentServiceOptions _enrichmentOptions = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public IntelligenceStoreConsumerTests()
    {
        _consumer  = Substitute.For<IEventConsumer>();
        _embedding = Substitute.For<IEmbeddingService>();
        _sql       = Substitute.For<IRecordStoreQueryExecutor>();

        _sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);

        _vectorSchema = Substitute.For<IVectorSchemaManager>();
        _vectorWrite  = Substitute.For<IVectorWriteService>();

        _vectorSchema.ApplyCollectionAsync(Arg.Any<CollectionSchema>()).Returns(Task.CompletedTask);
        _vectorWrite.UpsertNamedAsync(
            Arg.Any<string>(),
            Arg.Any<ulong>(),
            Arg.Any<IReadOnlyDictionary<string, float[]>>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>())
            .Returns(Task.CompletedTask);
        _vectorWrite.DeleteAsync(Arg.Any<string>(), Arg.Any<ulong>()).Returns(Task.CompletedTask);
        _vectorWrite.DeleteByFilterAsync(Arg.Any<string>(), Arg.Any<Filter>()).Returns(Task.CompletedTask);
        _vectorWrite.UpdateNamedVectorsAsync(
            Arg.Any<string>(),
            Arg.Any<ulong>(),
            Arg.Any<IReadOnlyDictionary<string, float[]>>())
            .Returns(Task.CompletedTask);
        _embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                  .Returns(new float[768]);

        _enrichment = Substitute.For<IEnrichmentService>();
        _enrichment.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns("Situating sentence.");

        _entities = Substitute.For<IEntityRepository>();
        // Default: authoritative row agrees with the event payload's owner value used across
        // the pre-existing (non-adversarial) tests in this file. TenantId is included because
        // tenant re-derivation (qdrant-tenant-collection-isolation) now reuses this same stub —
        // "test-tenant" matches every SchemaFixtures descriptor's TenantColumn = "TenantId".
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
                 .Returns("""{"AuthorId":"00000000-0000-0000-0000-000000000001","TenantId":"test-tenant"}""");

        _registry = new SchemaRegistry(
            new SchemaRegistryRepository(_sql),
            NullLogger<SchemaRegistry>.Instance);
    }

    private string Serialize(EntityEvent ev) => JsonSerializer.Serialize(ev, JsonOptions);

    private IntelligenceStoreConsumer BuildSut() =>
        new(
            _consumer,
            _vectorSchema,
            _vectorWrite,
            _embedding,
            _registry,
            _entities,
            new IntelligenceTenantScope("test-signing-key-0123456789abcdef"),
            _enrichment,
            Options.Create(_enrichmentOptions),
            NullLogger<IntelligenceStoreConsumer>.Instance);

    [Fact]
    public async Task HandleCreated_WithVectorField_CallsEmbedAndUpsertNamed()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var fakeVector = new float[768];
        _embedding.EmbedAsync("Great Title", Arg.Any<CancellationToken>())
                  .Returns(fakeVector);

        var payload = """{"Title":"Great Title","Body":"Some body text","AuthorId":"00000000-0000-0000-0000-000000000001"}""";
        var ev = new EntityEvent(
            EventType:     EntityEventType.Created,
            TypeName:      "Article",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   payload,
            TraceId:       "trace-1",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Intelligence);

        var sut = BuildSut();
        await sut.HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        _ = _embedding.Received().EmbedAsync(
            "Great Title",
            Arg.Any<CancellationToken>());

        await _vectorWrite.Received().UpsertNamedAsync(
            "articles_test-tenant",
            Arg.Any<ulong>(),
            Arg.Any<IReadOnlyDictionary<string, float[]>>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>());
    }

    [Fact]
    public async Task HandleCreated_WithChunkField_SplitsTextAndUpserts()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var longBody = new string('x', 3000);
        var payload  = $$$"""{"Title":"Test","Body":"{{{longBody}}}","AuthorId":"00000000-0000-0000-0000-000000000001"}""";
        var ev = new EntityEvent(
            EventType:     EntityEventType.Created,
            TypeName:      "Article",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   payload,
            TraceId:       "trace-2",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Intelligence);

        var sut = BuildSut();
        await sut.HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        // Should upsert at least once into the chunks collection
        await _vectorWrite.Received().UpsertNamedAsync(
            "articles_chunks_test-tenant",
            Arg.Any<ulong>(),
            Arg.Any<IReadOnlyDictionary<string, float[]>>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>());
    }

    [Fact]
    public async Task HandleCreated_WithChunkFieldAndOwnerField_WritesOwnerValueIntoChunkPayload()
    {
        var schema = SchemaFixtures.ArticleSchema() with
        {
            Authorization = new Iverson.Api.Schema.AuthorizationRules(
                "AuthorId",
                new List<Iverson.Api.Schema.RowPermission> { new("test-bypass", true, true, true) },
                new List<Iverson.Api.Schema.FieldPermission>())
        };
        await _registry.RegisterAsync(schema);

        var longBody = new string('x', 3000);
        var payload  = $$$"""{"Title":"Test","Body":"{{{longBody}}}","AuthorId":"00000000-0000-0000-0000-000000000001"}""";
        var ev = new EntityEvent(
            EventType: EntityEventType.Created, TypeName: "Article", Key: Guid.NewGuid().ToString(),
            PayloadJson: payload, TraceId: "trace-owner", SchemaVersion: "1",
            OccurredAt: DateTimeOffset.UtcNow, TargetStores: StoreTarget.Intelligence);

        IReadOnlyDictionary<string, object>? capturedPayload = null;
        _vectorWrite.UpsertNamedAsync(
                "articles_chunks_test-tenant",
                Arg.Any<ulong>(),
                Arg.Any<IReadOnlyDictionary<string, float[]>>(),
                Arg.Do<IReadOnlyDictionary<string, object>?>(p => capturedPayload = p))
            .Returns(Task.CompletedTask);

        await BuildSut().HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        capturedPayload.Should().NotBeNull();
        capturedPayload!["authorId"].Should().Be("00000000-0000-0000-0000-000000000001");
    }

    [Fact]
    public async Task HandleCreated_WithChunkFieldAndNoOwnerField_OmitsOwnerKeyFromChunkPayload()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema()); // BypassAuthorization() has OwnerField == null

        var longBody = new string('x', 3000);
        var payload  = $$$"""{"Title":"Test","Body":"{{{longBody}}}","AuthorId":"00000000-0000-0000-0000-000000000001"}""";
        var ev = new EntityEvent(
            EventType: EntityEventType.Created,
            TypeName: "Article",
            Key: Guid.NewGuid().ToString(),
            PayloadJson: payload,
            TraceId: "trace-no-owner",
            SchemaVersion: "1",
            OccurredAt: DateTimeOffset.UtcNow,
            TargetStores: StoreTarget.Intelligence);

        IReadOnlyDictionary<string, object>? capturedPayload = null;
        _vectorWrite.UpsertNamedAsync(
                "articles_chunks_test-tenant",
                Arg.Any<ulong>(),
                Arg.Any<IReadOnlyDictionary<string, float[]>>(),
                Arg.Do<IReadOnlyDictionary<string, object>?>(p => capturedPayload = p))
            .Returns(Task.CompletedTask);

        await BuildSut().HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        capturedPayload.Should().NotBeNull();
        capturedPayload!.Should().NotContainKey("authorId");
    }

    [Fact]
    public async Task HandleCreated_WithForgedOwnerValueInPayload_ChunkPayloadUsesAuthoritativeValueNotPayloadValue()
    {
        // CSR #7 regression: a forged/stale event whose payload owner value disagrees with the
        // authoritative Postgres row must NOT propagate the payload's (untrusted) value into Qdrant.
        var schema = SchemaFixtures.ArticleSchema() with
        {
            Authorization = new AuthorizationRules(
                "AuthorId",
                new List<RowPermission> { new("test-bypass", true, true, true) },
                new List<FieldPermission>())
        };
        await _registry.RegisterAsync(schema);

        const string forgedOwner = "00000000-0000-0000-0000-000000000FED";
        const string realOwner   = "00000000-0000-0000-0000-000000000001";
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
                 .Returns($$"""{"AuthorId":"{{realOwner}}","TenantId":"test-tenant"}""");

        var longBody = new string('x', 3000);
        var payload  = $$$"""{"Title":"Test","Body":"{{{longBody}}}","AuthorId":"{{{forgedOwner}}}"}""";
        var ev = new EntityEvent(
            EventType: EntityEventType.Created,
            TypeName: "Article",
            Key: Guid.NewGuid().ToString(),
            PayloadJson: payload,
            TraceId: "trace-forged-owner",
            SchemaVersion: "1",
            OccurredAt: DateTimeOffset.UtcNow,
            TargetStores: StoreTarget.Intelligence);

        IReadOnlyDictionary<string, object>? capturedPayload = null;
        _vectorWrite
            .UpsertNamedAsync(
                "articles_chunks_test-tenant",
                Arg.Any<ulong>(),
                Arg.Any<IReadOnlyDictionary<string, float[]>>(),
                Arg.Do<IReadOnlyDictionary<string, object>?>(p => capturedPayload = p))
            .Returns(Task.CompletedTask);

        await BuildSut().HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        capturedPayload.Should().NotBeNull();
        capturedPayload!["authorId"].Should().Be(realOwner);
        capturedPayload["authorId"].Should().NotBe(forgedOwner);
    }

    [Fact]
    public async Task HandleCreated_WithForgedOwnerValueInPayload_PointPayloadUsesAuthoritativeValueNotPayloadValue()
    {
        // Same CSR #7 regression, but for the named-vector pointPayload path — exercised with a
        // schema where the owner field is a genuine scalar column (matching the real registration
        // invariant enforced by ObjectMappingGrpcService: OwnerField must be a ScalarColumns member).
        var schema = new SchemaDescriptor
        {
            TypeName       = "Doc",
            TableName      = "docs",
            CollectionName = "docs",
            KeyColumn      = new ColumnDescriptor("Id", "uuid", false),
            ScalarColumns  = [new ColumnDescriptor("Title", "text", false), new ColumnDescriptor("OwnerId", "text", false)],
            FkColumns      = [],
            VectorFields   = [new VectorDescriptor("Title", 768, "nomic-embed-text")],
            ChunkFields    = [],
            Relations      = [],
            TenantColumn   = "TenantId",
            Authorization  = new AuthorizationRules(
                "OwnerId",
                new List<RowPermission> { new("test-bypass", true, true, true) },
                new List<FieldPermission>())
        };
        await _registry.RegisterAsync(schema);

        const string forgedOwner = "forged-owner";
        const string realOwner   = "real-owner";
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>())
            .Returns($$"""{"OwnerId":"{{realOwner}}","TenantId":"test-tenant"}""");

        var entityKey = Guid.NewGuid().ToString();
        var payload   = $$$"""{"Title":"Hello","OwnerId":"{{{forgedOwner}}}"}""";
        var ev = new EntityEvent(
            EventType: EntityEventType.Created,
            TypeName: "Doc",
            Key: entityKey,
            PayloadJson: payload,
            TraceId: "trace-forged-owner-point",
            SchemaVersion: "1",
            OccurredAt: DateTimeOffset.UtcNow,
            TargetStores: StoreTarget.Intelligence);

        IReadOnlyDictionary<string, object>? capturedPayload = null;
        _vectorWrite
            .UpsertNamedAsync(
                "docs_test-tenant",
                Arg.Any<ulong>(),
                Arg.Any<IReadOnlyDictionary<string, float[]>>(),
                Arg.Do<IReadOnlyDictionary<string, object>?>(p => capturedPayload = p))
            .Returns(Task.CompletedTask);

        await BuildSut().HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        capturedPayload.Should().NotBeNull();
        capturedPayload!["ownerId"].Should().Be(realOwner);
        capturedPayload["ownerId"].Should().NotBe(forgedOwner);
    }

    [Fact]
    public async Task HandleCreated_WithOwnerFieldAndNoAuthoritativeRow_OmitsOwnerKeyFromChunkPayload()
    {
        // Fail-closed: if the authoritative row can't be found (e.g. a delete-then-recreate race),
        // do NOT fall back to the event payload's unvalidated owner value — omit the key entirely.
        var schema = SchemaFixtures.ArticleSchema() with
        {
            Authorization = new AuthorizationRules(
                "AuthorId",
                new List<RowPermission> { new("test-bypass", true, true, true) },
                new List<FieldPermission>())
        };
        await _registry.RegisterAsync(schema);

        _entities
            .FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
            .Returns((string?)null);

        var longBody = new string('x', 3000);
        var payload  = $$$"""{"Title":"Test","Body":"{{{longBody}}}","AuthorId":"00000000-0000-0000-0000-000000000001"}""";
        var ev = new EntityEvent(
            EventType: EntityEventType.Created, TypeName: "Article", Key: Guid.NewGuid().ToString(),
            PayloadJson: payload, TraceId: "trace-missing-row", SchemaVersion: "1",
            OccurredAt: DateTimeOffset.UtcNow, TargetStores: StoreTarget.Intelligence);

        // The same stub now also serves the tenant fetch, so "no authoritative row found" means
        // no tenant value either — the write fails closed to the sentinel (no-tenant) collection.
        IReadOnlyDictionary<string, object>? capturedPayload = null;
        _vectorWrite
            .UpsertNamedAsync(
                "articles_chunks___no-tenant-claim__",
                Arg.Any<ulong>(),
                Arg.Any<IReadOnlyDictionary<string, float[]>>(),
                Arg.Do<IReadOnlyDictionary<string, object>?>(p => capturedPayload = p))
            .Returns(Task.CompletedTask);

        await BuildSut().HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        capturedPayload.Should().NotBeNull();
        capturedPayload!.Should().NotContainKey("authorId");
    }

    [Fact]
    public async Task HandleCreated_WithNoOwnerFieldConfigured_StillCallsFetchByKeyAsyncForTenant()
    {
        // Only the *owner*-value fetch is skipped when OwnerField is null — the *tenant*-value
        // fetch (qdrant-tenant-collection-isolation) is unconditional on TenantColumn alone, so
        // FetchByKeyAsync is still called exactly once for tenant re-derivation.
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema()); // BypassAuthorization() has OwnerField == null

        var longBody = new string('x', 3000);
        var payload  = $$$"""{"Title":"Test","Body":"{{{longBody}}}","AuthorId":"00000000-0000-0000-0000-000000000001"}""";
        var ev = new EntityEvent(
            EventType: EntityEventType.Created,
            TypeName: "Article",
            Key: Guid.NewGuid().ToString(),
            PayloadJson: payload,
            TraceId: "trace-no-owner-field",
            SchemaVersion: "1",
            OccurredAt: DateTimeOffset.UtcNow,
            TargetStores: StoreTarget.Intelligence);

        await BuildSut().HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _entities.Received(1).FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>());
    }

    [Fact]
    public async Task HandleDeleted_CallsVectorDelete()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var key = Guid.NewGuid().ToString();
        var ev = new EntityEvent(
            EventType:     EntityEventType.Deleted,
            TypeName:      "Article",
            Key:           key,
            PayloadJson:   """{"TenantId":"test-tenant"}""",
            TraceId:       "trace-3",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Intelligence);

        var sut = BuildSut();
        await sut.HandleDeleteAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _vectorWrite.Received(1).DeleteAsync("articles_test-tenant", Arg.Any<ulong>());
    }

    [Fact]
    public async Task HandleDeleteAsync_WithChunkFields_DeletesChunkPointsByParentIdFilter()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema()); // has ChunkFields (Body)

        var ev = new EntityEvent(
            EventType:     EntityEventType.Deleted,
            TypeName:      "Article",
            Key:           "article-123",
            PayloadJson:   """{"TenantId":"test-tenant"}""",
            TraceId:       "trace-chunk-delete",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Intelligence);

        var sut = BuildSut();
        await sut.HandleDeleteAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _vectorWrite.Received(1).DeleteByFilterAsync(
            "articles_chunks_test-tenant",
            Arg.Is<Filter>(f => f.Must.Count == 1 && f.Must[0].Field.Key == "parent_id"
                              && f.Must[0].Field.Match.Keyword == "article-123"));
    }

    [Fact]
    public async Task SkipsEvent_WhenNoIntelligenceFlag()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var ev = new EntityEvent(
            EventType:     EntityEventType.Created,
            TypeName:      "Article",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   """{"Title":"Test"}""",
            TraceId:       "trace-4",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Engagement); // no Intelligence

        var sut = BuildSut();
        await sut.HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        _ = _embedding.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _vectorWrite.DidNotReceive().UpsertNamedAsync(
            Arg.Any<string>(),
            Arg.Any<ulong>(),
            Arg.Any<IReadOnlyDictionary<string, float[]>>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>());
    }

    [Fact]
    public async Task SkipsEmptyTextField_DoesNotCallEmbed()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        // Title is empty string — should not embed
        var payload = """{"Title":"","Body":"some body","AuthorId":"00000000-0000-0000-0000-000000000001"}""";
        var ev = new EntityEvent(
            EventType:     EntityEventType.Created,
            TypeName:      "Article",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   payload,
            TraceId:       "trace-5",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Intelligence);

        var sut = BuildSut();
        await sut.HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        _ = _embedding.DidNotReceive().EmbedAsync("", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmbedFailure_Propagates()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        _embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                  .Returns<float[]>(_ => throw new Exception("Ollama timeout"));

        var payload = """{"Title":"Test Title","Body":"Some body","AuthorId":"00000000-0000-0000-0000-000000000001"}""";
        var ev = new EntityEvent(
            EventType:     EntityEventType.Created,
            TypeName:      "Article",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   payload,
            TraceId:       "trace-6",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Intelligence);

        var sut = BuildSut();
        var act = async () => await sut.HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await act.Should().ThrowAsync<Exception>().WithMessage("Ollama timeout");
    }

    [Fact]
    public async Task HandleCreated_PointPayload_ContainsKeyAndCamelCaseFields()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var entityKey = Guid.NewGuid().ToString();
        var titleText = "My Test Title";
        var payload   = $$$"""{"Title":"{{{titleText}}}","Body":"Some body text","AuthorId":"00000000-0000-0000-0000-000000000001"}""";
        var ev = new EntityEvent(
            EventType:     EntityEventType.Created,
            TypeName:      "Article",
            Key:           entityKey,
            PayloadJson:   payload,
            TraceId:       "trace-payload",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Intelligence);

        IReadOnlyDictionary<string, object>? capturedPayload = null;
        _vectorWrite
            .UpsertNamedAsync(
                "articles_test-tenant",
                Arg.Any<ulong>(),
                Arg.Any<IReadOnlyDictionary<string, float[]>>(),
                Arg.Do<IReadOnlyDictionary<string, object>?>(p => capturedPayload = p))
            .Returns(Task.CompletedTask);

        var sut = BuildSut();
        await sut.HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        capturedPayload.Should().NotBeNull();
        capturedPayload!["key"].Should().Be(entityKey);
        capturedPayload["title"].Should().Be(titleText);
    }

    [Fact]
    public async Task HandleCreated_WithMultipleVectorFields_EmbedsAllFields()
    {
        // Schema with two vector fields — verifies both EmbedAsync calls fire
        var twoVectorSchema = new SchemaDescriptor
        {
            TypeName       = "Doc",
            TableName      = "docs",
            CollectionName = "docs",
            KeyColumn      = new ColumnDescriptor("Id",    "uuid", false),
            ScalarColumns  = [new ColumnDescriptor("Title", "text", false),
                              new ColumnDescriptor("Summary", "text", false)],
            FkColumns      = [],
            VectorFields   = [
                new VectorDescriptor("Title",   768, "nomic-embed-text"),
                new VectorDescriptor("Summary", 768, "nomic-embed-text")
            ],
            ChunkFields    = [],
            Relations      = []
        };
        await _registry.RegisterAsync(twoVectorSchema);

        _embedding
            .EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[768]);

        var payload = """{"Title":"Hello","Summary":"World","Id":"00000000-0000-0000-0000-000000000001"}""";
        var ev = new EntityEvent(
            EventType:     EntityEventType.Created,
            TypeName:      "Doc",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   payload,
            TraceId:       "t-parallel",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Intelligence);

        var sut = BuildSut();
        await sut.HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        _ = _embedding.Received(1).EmbedAsync("Hello",  Arg.Any<CancellationToken>());
        _ = _embedding.Received(1).EmbedAsync("World",  Arg.Any<CancellationToken>());
        _ = _embedding.Received(2).EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChunkSplitting_ProducesMultipleChunks_ForLongText()
    {
        // Custom schema: maxTokens=50 (200 chars), overlap=10 (40 chars) → step=160 chars
        // A 3000-char body → at least 10 chunks (3000 / 160 ≈ 18.75)
        var customSchema = new SchemaDescriptor
        {
            TypeName       = "Doc",
            TableName      = "docs",
            CollectionName = "docs",
            KeyColumn      = new ColumnDescriptor("Id", "uuid", false),
            ScalarColumns  = [new ColumnDescriptor("Body", "text", false)],
            FkColumns      = [],
            VectorFields   = [],
            ChunkFields    = [new ChunkDescriptor("Body", 50, 10, "text-embedding-3-small", 1536)],
            Relations      = [],
            TenantColumn   = "TenantId"
        };
        await _registry.RegisterAsync(customSchema);

        var longBody = new string('a', 3000);
        var payload  = $$$"""{"Body":"{{{longBody}}}"}""";
        var ev = new EntityEvent(
            EventType:     EntityEventType.Created,
            TypeName:      "Doc",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   payload,
            TraceId:       "trace-7",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Intelligence);

        var upsertCount = 0;
        _vectorWrite
            .UpsertNamedAsync(
                "docs_chunks_test-tenant",
                Arg.Any<ulong>(),
                Arg.Any<IReadOnlyDictionary<string, float[]>>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>())
            .Returns(ci =>
            {
                upsertCount++;
                return Task.CompletedTask;
            });

        var sut = BuildSut();
        await sut.HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        upsertCount.Should().BeGreaterThanOrEqualTo(10);
    }

    [Fact]
    public async Task HandleCreated_PointPayload_ContainsTypedScalarAndFkColumns()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var entityKey  = Guid.NewGuid().ToString();
        var authorId   = "00000000-0000-0000-0000-000000000001";
        var payload    = $$$"""{"Title":"T","Body":"B","AuthorId":"{{{authorId}}}"}""";
        var ev = new EntityEvent(
            EventType:     EntityEventType.Created,
            TypeName:      "Article",
            Key:           entityKey,
            PayloadJson:   payload,
            TraceId:       "trace-typed",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Intelligence);

        IReadOnlyDictionary<string, object>? capturedPayload = null;
        _vectorWrite
            .UpsertNamedAsync(
                "articles_test-tenant",
                Arg.Any<ulong>(),
                Arg.Any<IReadOnlyDictionary<string, float[]>>(),
                Arg.Do<IReadOnlyDictionary<string, object>?>(p => capturedPayload = p))
            .Returns(Task.CompletedTask);

        var sut = BuildSut();
        await sut.HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        capturedPayload.Should().NotBeNull();
        capturedPayload!["authorId"].Should().Be(authorId);
    }

    [Fact]
    public void ComputeChunkPointId_SameInputs_ProducesSameIdAcrossHashSeeds()
    {
        // Regression test for the process-restart chunk-ID instability bug: the old
        // implementation folded in string.GetHashCode(), which .NET randomizes per
        // process, so the same (parentId, fieldName, chunkIndex) must produce the
        // same point ID regardless of AppDomain string-hash-seed — this test can't
        // literally restart the process, so instead it asserts the method's result
        // is a pure function of its inputs, computed twice, with no reliance on any
        // process-global mutable state (the strongest test obtainable in-process).
        var method = typeof(IntelligenceStoreConsumer).GetMethod(
            "ComputeChunkPointId", BindingFlags.NonPublic | BindingFlags.Static)!;

        var first  = (ulong)method.Invoke(null, [42UL, "Body", 3])!;
        var second = (ulong)method.Invoke(null, [42UL, "Body", 3])!;

        first.Should().Be(second);
        first.Should().NotBe(0UL);

        // Different fieldName must still produce a different id (collision resistance
        // is not weakened by removing GetHashCode()).
        var differentField = (ulong)method.Invoke(null, [42UL, "Title", 3])!;
        differentField.Should().NotBe(first);
    }

    [Fact]
    public void ComputeChunkPointId_IsStableAcrossHypotheticalProcessRestarts()
    {
        // Hard-codes the expected output for a fixed input so a future accidental
        // reintroduction of GetHashCode() (or any other process-seeded source) is
        // caught immediately — this exact numeric value must never change for these
        // inputs, in this process or any other.
        var method = typeof(IntelligenceStoreConsumer).GetMethod(
            "ComputeChunkPointId", BindingFlags.NonPublic | BindingFlags.Static)!;

        var result = (ulong)method.Invoke(null, [42UL, "Body", 3])!;

        // Compute the expected value independently (not by calling the method under test)
        // using the same FNV-1a + mixing formula, so this test would fail if the
        // implementation's formula changes even though it's still "deterministic".
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime       = 1099511628211UL;
        var fnv = offsetBasis;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes("Body"))
        {
            fnv ^= b;
            fnv *= prime;
        }
        var expected = 42UL ^ ((fnv * 1000003UL + 3UL) * 0x9E3779B97F4A7C15UL);

        result.Should().Be(expected);
    }

    [Fact]
    public void KeyToUlong_NonGuidKey_IsStableAcrossHashSeeds()
    {
        // Regression test for the same hash-instability bug ComputeChunkPointId was fixed
        // for: the non-GUID fallback branch is unreachable today (keys are server-generated
        // UUIDv7, always GUID-parseable) but feeds directly into ComputeChunkPointId's
        // parentId, so it must not rely on string.GetHashCode() either. As with
        // ComputeChunkPointId, this asserts the method is a pure function of its input.
        var method = typeof(IntelligenceStoreConsumer).GetMethod(
            "KeyToUlong", BindingFlags.NonPublic | BindingFlags.Static)!;

        var first  = (ulong)method.Invoke(null, ["not-a-guid-key"])!;
        var second = (ulong)method.Invoke(null, ["not-a-guid-key"])!;

        first.Should().Be(second);
        first.Should().NotBe(0UL);

        var differentKey = (ulong)method.Invoke(null, ["another-non-guid-key"])!;
        differentKey.Should().NotBe(first);
    }

    [Fact]
    public void KeyToUlong_NonGuidKey_UsesFnvHash()
    {
        // Hard-codes the expected output for a fixed input so a future accidental
        // reintroduction of GetHashCode() in the fallback branch is caught immediately.
        var method = typeof(IntelligenceStoreConsumer).GetMethod(
            "KeyToUlong", BindingFlags.NonPublic | BindingFlags.Static)!;

        var result = (ulong)method.Invoke(null, ["not-a-guid-key"])!;

        // Compute the expected value independently (not by calling the method under test)
        // using the same FNV-1a formula as FnvHash, so this test would fail if the
        // implementation's hash source changes even though it's still "deterministic".
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime       = 1099511628211UL;
        var expected = offsetBasis;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes("not-a-guid-key"))
        {
            expected ^= b;
            expected *= prime;
        }

        result.Should().Be(expected);
    }

    [Fact]
    public async Task EnsureCollectionAsync_CalledTwiceWithSameSchema_AppliesCollectionOnlyOnce()
    {
        var sut = BuildSut();
        var method = typeof(IntelligenceStoreConsumer).GetMethod(
            "EnsureCollectionAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var schema = new CollectionSchema("test_collection", [new NamedVector("v", 768)], []);

        await (Task)method.Invoke(sut, [schema])!;
        await (Task)method.Invoke(sut, [schema])!;

        await _vectorSchema.Received(1).ApplyCollectionAsync(
            Arg.Is<CollectionSchema>(s => s.CollectionName == "test_collection"));
    }

    [Fact]
    public async Task EnsureCollectionAsync_CalledWithTwoDifferentNamedSchemas_AppliesBoth()
    {
        var sut = BuildSut();
        var method = typeof(IntelligenceStoreConsumer).GetMethod(
            "EnsureCollectionAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var schemaA = new CollectionSchema("collection_a", [new NamedVector("v", 768)], []);
        var schemaB = new CollectionSchema("collection_b", [new NamedVector("v", 768)], []);

        await (Task)method.Invoke(sut, [schemaA])!;
        await (Task)method.Invoke(sut, [schemaB])!;

        await _vectorSchema.Received(1).ApplyCollectionAsync(
            Arg.Is<CollectionSchema>(s => s.CollectionName == "collection_a"));
        await _vectorSchema.Received(1).ApplyCollectionAsync(
            Arg.Is<CollectionSchema>(s => s.CollectionName == "collection_b"));
    }

    [Fact]
    public async Task DispatchAsync_CreatedEvent_RoutesToUpsert()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var payload = """{"Title":"Great Title","Body":"Some body text","AuthorId":"00000000-0000-0000-0000-000000000001"}""";
        var ev = new EntityEvent(
            EventType:     EntityEventType.Created,
            TypeName:      "Article",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   payload,
            TraceId:       "trace-dispatch-1",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Intelligence);

        await BuildSut().DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _vectorWrite
            .Received()
            .UpsertNamedAsync(
                "articles_test-tenant",
                Arg.Any<ulong>(),
                Arg.Any<IReadOnlyDictionary<string, float[]>>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>());
        await _vectorWrite.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<ulong>());
    }

    [Fact]
    public async Task DispatchAsync_DeletedEvent_RoutesToDelete()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
        var key = Guid.NewGuid().ToString();

        var ev = new EntityEvent(
            EventType:     EntityEventType.Deleted,
            TypeName:      "Article",
            Key:           key,
            PayloadJson:   """{"TenantId":"test-tenant"}""",
            TraceId:       "trace-dispatch-2",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Intelligence);

        await BuildSut().DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _vectorWrite.Received(1).DeleteAsync("articles_test-tenant", Arg.Any<ulong>());
        await _vectorWrite
            .DidNotReceive()
            .UpsertNamedAsync(
                Arg.Any<string>(),
                Arg.Any<ulong>(),
                Arg.Any<IReadOnlyDictionary<string, float[]>>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>());
        }

    // ── Metadata denormalization onto chunk points ────────────────────────────

    private static SchemaDescriptor MetadataDocSchema(
        IEnumerable<ColumnDescriptor> scalarColumns,
        IEnumerable<string> metadataColumns,
        string? ownerField = null) => new()
    {
        TypeName       = "Doc",
        TableName      = "docs",
        CollectionName = "docs",
        KeyColumn      = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns  = scalarColumns.ToList(),
        FkColumns      = [],
        VectorFields   = [new VectorDescriptor("Title", 768, "nomic-embed-text")],
        ChunkFields    = [new ChunkDescriptor("Body", 512, 64, "nomic-embed-text", 768)],
        Relations      = [],
        TenantColumn   = "TenantId",
        MetadataColumns = new HashSet<string>(metadataColumns),
        Authorization  = new AuthorizationRules(
            ownerField,
            new List<RowPermission> { new("test-bypass", true, true, true) },
            new List<FieldPermission>())
    };

    private static EntityEvent DocEvent(string payloadJson, string traceId) => new(
        EventType:     EntityEventType.Created,
        TypeName:      "Doc",
        Key:           Guid.NewGuid().ToString(),
        PayloadJson:   payloadJson,
        TraceId:       traceId,
        SchemaVersion: "1",
        OccurredAt:    DateTimeOffset.UtcNow,
        TargetStores:  StoreTarget.Intelligence);

    [Fact]
    public async Task HandleCreated_WithMetadataColumns_WritesTypedValuesOntoChunkPayload()
    {
        var schema = MetadataDocSchema(
            [
                new ColumnDescriptor("Title", "text", false),
                new ColumnDescriptor("Body", "text", false),
                new ColumnDescriptor("Category", "text", false),
                new ColumnDescriptor("Rank", "INTEGER", true)
            ],
            ["Category", "Rank"]);
        await _registry.RegisterAsync(schema);

        var longBody = new string('x', 3000);
        var payload  = $$$"""{"Title":"T","Body":"{{{longBody}}}","Category":"news","Rank":7,"TenantId":"test-tenant"}""";

        IReadOnlyDictionary<string, object>? capturedPayload = null;
        _vectorWrite
            .UpsertNamedAsync(
                "docs_chunks_test-tenant",
                Arg.Any<ulong>(),
                Arg.Any<IReadOnlyDictionary<string, float[]>>(),
                Arg.Do<IReadOnlyDictionary<string, object>?>(p => capturedPayload = p))
            .Returns(Task.CompletedTask);

        var ev = DocEvent(payload, "trace-metadata-chunk");
        await BuildSut().HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        capturedPayload.Should().NotBeNull();
        capturedPayload!["category"].Should().Be("news");
        capturedPayload["rank"].Should().Be(7L);
    }

    [Fact]
    public async Task HandleCreated_WithMetadataColumns_LeavesObjectPointPayloadUnchanged()
    {
        var schema = MetadataDocSchema(
            [
                new ColumnDescriptor("Title", "text", false),
                new ColumnDescriptor("Body", "text", false),
                new ColumnDescriptor("Category", "text", false)
            ],
            ["Category"]);
        await _registry.RegisterAsync(schema);

        var longBody = new string('x', 3000);
        var payload  = $$$"""{"Title":"T","Body":"{{{longBody}}}","Category":"news","TenantId":"test-tenant"}""";

        IReadOnlyDictionary<string, object>? pointPayload = null;
        _vectorWrite
            .UpsertNamedAsync(
                "docs_test-tenant",
                Arg.Any<ulong>(),
                Arg.Any<IReadOnlyDictionary<string, float[]>>(),
                Arg.Do<IReadOnlyDictionary<string, object>?>(p => pointPayload = p))
            .Returns(Task.CompletedTask);

        var ev = DocEvent(payload, "trace-metadata-point");
        await BuildSut().HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        pointPayload.Should().NotBeNull();
        // The object-level point already mirrors every scalar; the metadata flag must add nothing.
        pointPayload!.Keys.Should().BeEquivalentTo(["key", "title", "body", "category"]);
    }

    [Fact]
    public async Task HandleCreated_WithMetadataFlaggedOwnerField_ChunkPayloadKeepsAuthoritativeOwnerValue()
    {
        const string forgedOwner = "forged-owner";
        const string realOwner   = "real-owner";

        var schema = MetadataDocSchema(
            [
                new ColumnDescriptor("Title", "text", false),
                new ColumnDescriptor("Body", "text", false),
                new ColumnDescriptor("OwnerId", "text", false)
            ],
            ["OwnerId"],
            ownerField: "OwnerId");
        await _registry.RegisterAsync(schema);

        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
                 .Returns($$"""{"OwnerId":"{{realOwner}}","TenantId":"test-tenant"}""");

        var longBody = new string('x', 3000);
        var payload  = $$$"""{"Title":"T","Body":"{{{longBody}}}","OwnerId":"{{{forgedOwner}}}","TenantId":"test-tenant"}""";

        IReadOnlyDictionary<string, object>? capturedPayload = null;
        _vectorWrite
            .UpsertNamedAsync(
                "docs_chunks_test-tenant",
                Arg.Any<ulong>(),
                Arg.Any<IReadOnlyDictionary<string, float[]>>(),
                Arg.Do<IReadOnlyDictionary<string, object>?>(p => capturedPayload = p))
            .Returns(Task.CompletedTask);

        var ev = DocEvent(payload, "trace-metadata-owner");
        await BuildSut().HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        capturedPayload.Should().NotBeNull();
        capturedPayload!["ownerId"].Should().Be(realOwner);
        capturedPayload["ownerId"].Should().NotBe(forgedOwner);
    }

    // ── Contextual chunk prefixes ─────────────────────────────────────────────

    // maxTokens = 1000 → 4000-char window, so ContextualBody below is a single chunk and every
    // assertion about "the prompt" / "the embedded text" is deterministic.
    private static SchemaDescriptor ContextualDocSchema(
        bool contextual,
        bool withSummaryTarget) => new()
    {
        TypeName       = "Doc",
        TableName      = "docs",
        CollectionName = "docs",
        KeyColumn      = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns  = [new ColumnDescriptor("Body", "text", false),
                          new ColumnDescriptor("Summary", "text", true)],
        FkColumns      = [],
        VectorFields   = [],
        ChunkFields    = [new ChunkDescriptor("Body", 1000, 0, "nomic-embed-text", 768, contextual)],
        Relations      = [],
        TenantColumn   = "TenantId",
        EnrichmentTargets = withSummaryTarget
            ? [new EnrichmentTarget("Summary", EnrichmentKind.Summary, null)]
            : []
    };

    // 2500 chars — longer than the 2000-char parent-text fallback slice, shorter than one chunk.
    private static readonly string ContextualBody = "B" + new string('a', 2498) + "Z";

    private List<string> CaptureEnrichmentPrompts(string generated = "Situating sentence.")
    {
        var prompts = new List<string>();
        _enrichment.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(ci =>
                   {
                       lock (prompts) prompts.Add((string)ci[0]);
                       return Task.FromResult(generated);
                   });
        return prompts;
    }

    private (List<string> EmbeddedTexts, List<IReadOnlyDictionary<string, object>?> Payloads) CaptureChunkWrites()
    {
        var embedded = new List<string>();
        _embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                  .Returns(ci =>
                  {
                      lock (embedded) embedded.Add((string)ci[0]);
                      return Task.FromResult(new float[768]);
                  });

        var payloads = new List<IReadOnlyDictionary<string, object>?>();
        _vectorWrite
            .UpsertNamedAsync(
                "docs_chunks_test-tenant",
                Arg.Any<ulong>(),
                Arg.Any<IReadOnlyDictionary<string, float[]>>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>())
            .Returns(ci =>
            {
                lock (payloads) payloads.Add((IReadOnlyDictionary<string, object>?)ci[3]);
                return Task.CompletedTask;
            });

        return (embedded, payloads);
    }

    [Fact]
    public async Task HandleCreated_ContextualChunkField_EmbedsPrefixedTextButStoresRawChunkText()
    {
        await _registry.RegisterAsync(ContextualDocSchema(contextual: true, withSummaryTarget: true));
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
                 .Returns("""{"Summary":"The doc is about widgets.","TenantId":"test-tenant"}""");

        var prompts = CaptureEnrichmentPrompts();
        var (embedded, payloads) = CaptureChunkWrites();

        var ev = DocEvent($$$"""{"Body":"{{{ContextualBody}}}","TenantId":"test-tenant"}""", "trace-contextual");
        await BuildSut().HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        // The prompt is conditioned on the object's summary, located via EnrichmentTargets.
        prompts.Should().ContainSingle();
        prompts[0].Should().Be(string.Format(
            EnrichmentPrompts.ChunkContext, "The doc is about widgets.", ContextualBody));

        // The embedded text carries the generated prefix …
        embedded.Should().ContainSingle();
        embedded[0].Should().Be("Situating sentence.\n\n" + ContextualBody);

        // … but the stored payload's "text" key stays the raw, unprefixed chunk, and the
        // prefix is not persisted under any other key.
        payloads.Should().ContainSingle();
        payloads[0]!["text"].Should().Be(ContextualBody);
        payloads[0]!.Values.Any(v => v is string s && s.Contains("Situating sentence."))
                    .Should().BeFalse();
    }

    [Fact]
    public async Task HandleCreated_NonContextualChunkField_EmbedsRawTextAndMakesNoGenerativeCall()
    {
        await _registry.RegisterAsync(ContextualDocSchema(contextual: false, withSummaryTarget: true));
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
                 .Returns("""{"Summary":"The doc is about widgets.","TenantId":"test-tenant"}""");

        var (embedded, payloads) = CaptureChunkWrites();

        var ev = DocEvent($$$"""{"Body":"{{{ContextualBody}}}","TenantId":"test-tenant"}""", "trace-non-contextual");
        await BuildSut().HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _enrichment.DidNotReceive().GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        embedded.Should().ContainSingle().Which.Should().Be(ContextualBody);
        payloads.Should().ContainSingle();
        payloads[0]!["text"].Should().Be(ContextualBody);
    }

    [Fact]
    public async Task HandleCreated_ContextualChunkFieldWithNoSummary_FallsBackToTruncatedParentText()
    {
        // No Summary enrichment target at all — the first-ingest case, before the enricher's
        // republish drives a second, summary-conditioned pass.
        await _registry.RegisterAsync(ContextualDocSchema(contextual: true, withSummaryTarget: false));

        var prompts = CaptureEnrichmentPrompts();
        CaptureChunkWrites();

        var ev = DocEvent($$$"""{"Body":"{{{ContextualBody}}}","TenantId":"test-tenant"}""", "trace-no-summary");
        await BuildSut().HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        prompts.Should().ContainSingle();
        prompts[0].Should().Be(string.Format(
            EnrichmentPrompts.ChunkContext, ContextualBody[..2000], ContextualBody));
    }

    [Fact]
    public async Task HandleCreated_ContextualChunkFieldWithSummaryColumnNull_FallsBackToTruncatedParentText()
    {
        // A Summary target is declared but the column is still null (also first ingest).
        await _registry.RegisterAsync(ContextualDocSchema(contextual: true, withSummaryTarget: true));
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
                 .Returns("""{"Summary":null,"TenantId":"test-tenant"}""");

        var prompts = CaptureEnrichmentPrompts();
        CaptureChunkWrites();

        var ev = DocEvent($$$"""{"Body":"{{{ContextualBody}}}","TenantId":"test-tenant"}""", "trace-null-summary");
        await BuildSut().HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        prompts.Should().ContainSingle();
        prompts[0].Should().Be(string.Format(
            EnrichmentPrompts.ChunkContext, ContextualBody[..2000], ContextualBody));
    }

    [Fact]
    public async Task HandleCreated_ContextualChunkFieldAndGenerationThrows_StillUpsertsChunkWithUnprefixedText()
    {
        // Spec §6: enrichment must never block or fail an object's projection.
        await _registry.RegisterAsync(ContextualDocSchema(contextual: true, withSummaryTarget: true));

        _enrichment.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns<string>(_ => throw new Exception("Ollama down"));

        var (embedded, payloads) = CaptureChunkWrites();

        var ev = DocEvent($$$"""{"Body":"{{{ContextualBody}}}","TenantId":"test-tenant"}""", "trace-gen-throws");
        var act = async () => await BuildSut().HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await act.Should().NotThrowAsync();
        embedded.Should().ContainSingle().Which.Should().Be(ContextualBody);
        payloads.Should().ContainSingle();
        payloads[0]!["text"].Should().Be(ContextualBody);
    }

    [Fact]
    public async Task HandleCreated_ContextualChunkFieldWithEnrichmentDisabled_MakesNoGenerativeCall()
    {
        _enrichmentOptions.Enabled = false; // Enrichment__Enabled global kill-switch
        await _registry.RegisterAsync(ContextualDocSchema(contextual: true, withSummaryTarget: true));

        var (embedded, payloads) = CaptureChunkWrites();

        var ev = DocEvent($$$"""{"Body":"{{{ContextualBody}}}","TenantId":"test-tenant"}""", "trace-disabled");
        await BuildSut().HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _enrichment.DidNotReceive().GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        embedded.Should().ContainSingle().Which.Should().Be(ContextualBody);
        payloads.Should().ContainSingle();
        payloads[0]!["text"].Should().Be(ContextualBody);
    }

    // ── Contextual prefix fan-out: concurrency cap + per-chunk failure isolation ──

    // maxTokens = 10 → 40-char window, overlap 0. MultiChunkBody below is 20 distinguishable
    // 40-char blocks, so it splits into exactly 20 chunks and each chunk names itself ("C00".."C19").
    private static SchemaDescriptor MultiChunkDocSchema() => new()
    {
        TypeName       = "Doc",
        TableName      = "docs",
        CollectionName = "docs",
        KeyColumn      = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns  = [new ColumnDescriptor("Body", "text", false),
                          new ColumnDescriptor("Summary", "text", true)],
        FkColumns      = [],
        VectorFields   = [],
        ChunkFields    = [new ChunkDescriptor("Body", 10, 0, "nomic-embed-text", 768, true)],
        Relations      = [],
        TenantColumn   = "TenantId",
        EnrichmentTargets = [new EnrichmentTarget("Summary", EnrichmentKind.Summary, null)]
    };

    private const int MultiChunkCount = 20;

    private static readonly string MultiChunkBody =
        string.Concat(Enumerable.Range(0, MultiChunkCount).Select(i => $"C{i:00}" + new string('x', 37)));

    [Fact]
    public async Task HandleCreated_ContextualChunkFanOut_NeverExceedsConfiguredConcurrencyCap()
    {
        const int cap = 3;
        _enrichmentOptions.MaxConcurrentChunkPrefixes = cap;
        await _registry.RegisterAsync(MultiChunkDocSchema());
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
                 .Returns("""{"Summary":"The doc is about widgets.","TenantId":"test-tenant"}""");

        var inFlight    = 0;
        var maxInFlight = 0;
        var calls       = 0;
        _enrichment.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(async _ =>
                   {
                       var now = Interlocked.Increment(ref inFlight);
                       Interlocked.Increment(ref calls);
                       // Record the high-water mark without losing races.
                       int observed;
                       while (now > (observed = Volatile.Read(ref maxInFlight)))
                           Interlocked.CompareExchange(ref maxInFlight, now, observed);

                       await Task.Delay(15);
                       Interlocked.Decrement(ref inFlight);
                       return "Situating sentence.";
                   });

        CaptureChunkWrites();

        var ev = DocEvent($$$"""{"Body":"{{{MultiChunkBody}}}","TenantId":"test-tenant"}""", "trace-fanout-cap");
        await BuildSut().HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        calls.Should().Be(MultiChunkCount, "every chunk still gets a prefix attempt");
        maxInFlight.Should().BeLessThanOrEqualTo(cap,
            "the semaphore must cap generative fan-out on the projection critical path");
    }

    [Fact]
    public async Task HandleCreated_ContextualChunkFanOut_OneChunkFailingDoesNotCostSiblingsTheirPrefixes()
    {
        await _registry.RegisterAsync(MultiChunkDocSchema());
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
                 .Returns("""{"Summary":"The doc is about widgets.","TenantId":"test-tenant"}""");

        // Only the chunk that names itself "C07" fails generation.
        _enrichment.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(ci => ((string)ci[0]).Contains("C07")
                       ? throw new Exception("Ollama down")
                       : Task.FromResult("Situating sentence."));

        var (embedded, _) = CaptureChunkWrites();

        var ev = DocEvent($$$"""{"Body":"{{{MultiChunkBody}}}","TenantId":"test-tenant"}""", "trace-fanout-isolation");
        var act = async () => await BuildSut().HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await act.Should().NotThrowAsync();
        embedded.Should().HaveCount(MultiChunkCount);
        embedded.Count(t => t.StartsWith("Situating sentence.\n\n")).Should().Be(MultiChunkCount - 1);
        embedded.Should().ContainSingle(t => !t.StartsWith("Situating sentence.") && t.StartsWith("C07"));
    }

    // A metadata column whose camelCase name collides with a reserved chunk payload key is now
    // rejected by SchemaBuilder at registration, so it cannot reach this consumer. Coverage moved
    // to SchemaBuilderTests.BuildDescriptor_Throws_WhenMetadataPropertyCollidesWithReservedChunkPayloadKey.

    // ── ComputeCentroid ─────────────────────────────────────────────────────

    [Fact]
    public void ComputeCentroid_KnownVectors_ReturnsMeanOfL2NormalizedInputs()
    {
        // [3,4] normalizes to [0.6,0.8]; [1,0] is already unit. Mean is [0.8,0.4].
        List<float[]> vectors = [[3f, 4f], [1f, 0f]];

        var result = IntelligenceStoreConsumer.ComputeCentroid(vectors);

        result[0].Should().BeApproximately(0.8f, 1e-6f);
        result[1].Should().BeApproximately(0.4f, 1e-6f);
    }

    [Fact]
    public void ComputeCentroid_SingleChunk_ReturnsThatChunkNormalized()
    {
        List<float[]> vectors = [[3f, 4f]];

        var result = IntelligenceStoreConsumer.ComputeCentroid(vectors);

        result[0].Should().BeApproximately(0.6f, 1e-6f);
        result[1].Should().BeApproximately(0.8f, 1e-6f);
    }

    // ── Centroid write ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleCreated_WithVectorFieldAndChunkField_UpdatesNamedVectorsWithCentroidOnObjectCollection()
    {
        // ArticleSchema has both a vector field (Title) and a chunk field (Body) — the object
        // point is written by the vector-field block, so the centroid write must go through
        // UpdateNamedVectorsAsync (partial update), not a second, clobbering upsert.
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var longBody = new string('x', 3000);
        var payload  = $$$"""{"Title":"Great Title","Body":"{{{longBody}}}","AuthorId":"00000000-0000-0000-0000-000000000001"}""";
        var ev = new EntityEvent(
            EventType:     EntityEventType.Created,
            TypeName:      "Article",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   payload,
            TraceId:       "trace-centroid-update",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Intelligence);

        await BuildSut().HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _vectorWrite.Received(1).UpdateNamedVectorsAsync(
            "articles_test-tenant",
            Arg.Any<ulong>(),
            Arg.Is<IReadOnlyDictionary<string, float[]>>(v => v.ContainsKey("body_centroid")));

        // The object block's own upsert still fires exactly once — proving the centroid write
        // added an *update*, not a second upsert that would clobber the object point's vectors.
        await _vectorWrite.Received(1).UpsertNamedAsync(
            "articles_test-tenant",
            Arg.Any<ulong>(),
            Arg.Any<IReadOnlyDictionary<string, float[]>>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>());
    }

    [Fact]
    public async Task HandleCreated_ChunksOnlyEntity_UpsertsCentroidAndPayloadOnObjectCollection()
    {
        // Chunks-only schema — no vector fields, so the object block never runs and the
        // object point does not yet exist. The centroid write must be the upsert branch.
        var schema = new SchemaDescriptor
        {
            TypeName       = "Doc",
            TableName      = "docs",
            CollectionName = "docs",
            KeyColumn      = new ColumnDescriptor("Id", "uuid", false),
            ScalarColumns  = [new ColumnDescriptor("Body", "text", false)],
            FkColumns      = [],
            VectorFields   = [],
            ChunkFields    = [new ChunkDescriptor("Body", 512, 64, "nomic-embed-text", 768)],
            Relations      = [],
            TenantColumn   = "TenantId"
        };
        await _registry.RegisterAsync(schema);

        var longBody = new string('x', 3000);
        var payload  = $$$"""{"Body":"{{{longBody}}}"}""";
        var ev = new EntityEvent(
            EventType:     EntityEventType.Created,
            TypeName:      "Doc",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   payload,
            TraceId:       "trace-centroid-upsert",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Intelligence);

        IReadOnlyDictionary<string, float[]>? capturedVectors = null;
        IReadOnlyDictionary<string, object>?  capturedPayload = null;
        _vectorWrite
            .UpsertNamedAsync(
                "docs_test-tenant",
                Arg.Any<ulong>(),
                Arg.Do<IReadOnlyDictionary<string, float[]>>(v => capturedVectors = v),
                Arg.Do<IReadOnlyDictionary<string, object>?>(p => capturedPayload = p))
            .Returns(Task.CompletedTask);

        await BuildSut().HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _vectorWrite.Received(1).UpsertNamedAsync(
            "docs_test-tenant",
            Arg.Any<ulong>(),
            Arg.Any<IReadOnlyDictionary<string, float[]>>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>());
        await _vectorWrite.DidNotReceive().UpdateNamedVectorsAsync(
            "docs_test-tenant", Arg.Any<ulong>(), Arg.Any<IReadOnlyDictionary<string, float[]>>());

        capturedVectors.Should().NotBeNull();
        capturedVectors!.Should().ContainKey("body_centroid");
        capturedPayload.Should().NotBeNull();
        capturedPayload!["key"].Should().Be(ev.Key);
    }

    [Fact]
    public async Task HandleCreated_BlankChunkField_WritesNoCentroidKey()
    {
        var schema = new SchemaDescriptor
        {
            TypeName       = "Doc",
            TableName      = "docs",
            CollectionName = "docs",
            KeyColumn      = new ColumnDescriptor("Id", "uuid", false),
            ScalarColumns  = [new ColumnDescriptor("Body", "text", false)],
            FkColumns      = [],
            VectorFields   = [],
            ChunkFields    = [new ChunkDescriptor("Body", 512, 64, "nomic-embed-text", 768)],
            Relations      = [],
            TenantColumn   = "TenantId"
        };
        await _registry.RegisterAsync(schema);

        var payload = """{"Body":""}""";
        var ev = new EntityEvent(
            EventType:     EntityEventType.Created,
            TypeName:      "Doc",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   payload,
            TraceId:       "trace-centroid-blank",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Intelligence);

        await BuildSut().HandleAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _vectorWrite.DidNotReceive().UpsertNamedAsync(
            "docs_test-tenant",
            Arg.Any<ulong>(),
            Arg.Any<IReadOnlyDictionary<string, float[]>>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>());
        await _vectorWrite.DidNotReceive().UpdateNamedVectorsAsync(
            "docs_test-tenant", Arg.Any<ulong>(), Arg.Any<IReadOnlyDictionary<string, float[]>>());
    }
}
