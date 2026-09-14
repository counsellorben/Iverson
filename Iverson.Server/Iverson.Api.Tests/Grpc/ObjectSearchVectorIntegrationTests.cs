using DotNet.Testcontainers.Builders;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using System.Text.Json;
using Iverson.Api.Authorization;
using Iverson.Api.Consumers;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.Events;
using Iverson.Sql;
using Iverson.StarRocks;
using Iverson.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Qdrant.Client;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

public sealed class QdrantGrpcContainerFixture : IAsyncLifetime
{
    private const int GrpcPort = 6334;
    private readonly DotNet.Testcontainers.Containers.IContainer _container =
        new ContainerBuilder()
            .WithImage("qdrant/qdrant:v1.18.2")
            .WithPortBinding(GrpcPort, assignRandomHostPort: true)
            .WithPortBinding(6333, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(GrpcPort))
            .Build();

    public IntelligenceVectorService Service { get; private set; } = null!;
    public IntelligenceCollectionManager CollectionManager { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var client = new QdrantClient(_container.Hostname, _container.GetMappedPublicPort(GrpcPort), https: false);
        Service           = new IntelligenceVectorService(client);
        CollectionManager = new IntelligenceCollectionManager(
            client,
            "test-api-key",
            NullLogger<IntelligenceCollectionManager>.Instance);
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[Trait("Category", "Integration")]
[Collection(ContainerCollection.Name)]
public sealed class ObjectSearchVectorIntegrationTests : IClassFixture<QdrantGrpcContainerFixture>
{
    private readonly IntelligenceVectorService _vector;
    private readonly IntelligenceCollectionManager _mgr;
    private readonly IEmbeddingService _embedding = Substitute.For<IEmbeddingService>();
    private readonly IEmbeddingServiceResolver _resolver = Substitute.For<IEmbeddingServiceResolver>();
    private readonly SchemaRegistry _registry;
    private readonly IntelligenceTenantScope _tenantScope = new("test-integration-signing-key-0123456789abcdef");

    // Matches ActingUserFixtures.Principal's default tenant_id claim — every tenant-qualified
    // collection name in this test file is resolved through _tenantScope with this value so
    // direct test-setup writes land in the same physical collection SearchSimilar/SearchChunks
    // will query via the RPC layer.
    private const string TestTenant = "test-tenant";

    public ObjectSearchVectorIntegrationTests(QdrantGrpcContainerFixture fx)
    {
        _vector = fx.Service;
        _mgr    = fx.CollectionManager;
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);
        _registry = new SchemaRegistry(new SchemaRegistryRepository(sql), NullLogger<SchemaRegistry>.Instance);
        _resolver.Get(Arg.Any<string?>()).Returns(_embedding);
    }

    private static string UniqueName() => "art_" + Guid.NewGuid().ToString("N")[..8];

    private ObjectSearchGrpcService BuildSut(
        PopularitySignalOptions? popularity = null, double wPopularity = 0.0) =>
        new(
            _registry,
            Substitute.For<IEngagementStoreSearchService>(),
            _vector,
            _resolver,
            NullLogger<ObjectSearchGrpcService>.Instance,
            new ActingUserAccessor { ActingUser = ActingUserFixtures.Principal("test-user", "test-bypass") },
            new RowFieldAuthorizationEvaluator(),
            _tenantScope,
            new ResultReranker(Options.Create(new VectorRankingOptions { WPopularity = wPopularity })),
            new ResultDiversifier(),
            Options.Create(new VectorRankingOptions { LambdaSimilar = 0.70, LambdaChunks = 0.70 }),
            Options.Create(new DecayOptions()),
            Options.Create(popularity ?? new PopularitySignalOptions()));

    private static (IServerStreamWriter<T> writer, List<T> written) MakeStream<T>()
    {
        var written = new List<T>();
        var writer  = Substitute.For<IServerStreamWriter<T>>();
        writer.WriteAsync(Arg.Do<T>(written.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return (writer, written);
    }

    [Fact]
    public async Task SearchSimilar_WithRangeFilter_ReturnsOnlyMatchingTypedPayload()
    {
        var collection = UniqueName();
        var baseSchema = SchemaFixtures.ArticleSchema();
        var schema = baseSchema with
        {
            CollectionName = collection,
            // SchemaFixtures.ArticleSchema() only declares Title/Body; add WordCount so
            // ValidateFilterProperty accepts it as a real scalar column for this test.
            ScalarColumns = [.. baseSchema.ScalarColumns, new ColumnDescriptor("WordCount", "integer", false)]
        };
        await _registry.RegisterAsync(schema);
        var physicalCollection = _tenantScope.ResolveCollectionName(collection, TestTenant, isChunks: false);
        await _mgr.ApplyCollectionAsync(new CollectionSchema(
            physicalCollection, [new NamedVector("title_vector", 4)], []));

        var vec = new float[] { 0.1f, 0.2f, 0.3f, 0.4f };
        _embedding.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(vec);

        await _vector.UpsertNamedAsync(physicalCollection, 1,
            new Dictionary<string, float[]> { ["title_vector"] = vec },
            new Dictionary<string, object> { ["wordCount"] = 100L, ["tenantId"] = "test-tenant" });
        await _vector.UpsertNamedAsync(physicalCollection, 2,
            new Dictionary<string, float[]> { ["title_vector"] = vec },
            new Dictionary<string, object> { ["wordCount"] = 900L, ["tenantId"] = "test-tenant" });

        var sut = BuildSut();
        var request = new SearchSimilarRequest { TypeName = "Article", Property = "Title", Query = "q", TopK = 10 };
        request.Filter.Add(new SearchClause
        {
            Property = "WordCount", Operator = SearchOperator.GreaterThan,
            Value = new SearchValue { NumberVal = 500 }, ClauseType = SearchClauseType.Filter
        });

        var (writer, written) = MakeStream<SearchResponse>();
        await sut.SearchSimilar(request, writer, TestServerCallContext.Create());

        written.Should().ContainSingle();
    }

    [Fact]
    public async Task SearchSimilar_ReturnsCanonicalDescriptorNamesAndTypedValues()
    {
        var collection = UniqueName();
        var baseSchema = SchemaFixtures.ArticleSchema();
        var schema = baseSchema with
        {
            CollectionName = collection,
            // WordCount carries the lowercase SqlType convention SchemaFixtures.cs:57-64 uses
            // everywhere else; ViewCount carries the uppercase convention SchemaBuilder.cs:351-359
            // actually emits in production. The type-mapping switch must handle both — a test that
            // exercised only one casing could not falsify a comparison that only handles the other.
            ScalarColumns = [
                .. baseSchema.ScalarColumns,
                new ColumnDescriptor("WordCount", "integer", false),
                new ColumnDescriptor("ViewCount", "BIGINT", false)]
        };
        await _registry.RegisterAsync(schema);
        var physicalCollection = _tenantScope.ResolveCollectionName(collection, TestTenant, isChunks: false);
        await _mgr.ApplyCollectionAsync(new CollectionSchema(
            physicalCollection, [new NamedVector("title_vector", 4)], []));

        var vec = new float[] { 0.1f, 0.2f, 0.3f, 0.4f };
        _embedding.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(vec);

        // "key" is the literal payload key IntelligenceStoreConsumer.cs:417 writes the identity
        // value under — it must be seeded explicitly here, or the Id-vs-Key assertion below has no
        // subject to assert against.
        await _vector.UpsertNamedAsync(physicalCollection, 1,
            new Dictionary<string, float[]> { ["title_vector"] = vec },
            new Dictionary<string, object>
            {
                ["key"]       = "article-1",
                ["wordCount"] = 100L,
                ["viewCount"] = 250L,
                ["tenantId"]  = "test-tenant"
            });

        var sut = BuildSut();
        var request = new SearchSimilarRequest { TypeName = "Article", Property = "Title", Query = "q", TopK = 10 };

        var (writer, written) = MakeStream<SearchResponse>();
        await sut.SearchSimilar(request, writer, TestServerCallContext.Create());

        written.Should().ContainSingle();
        var fields = written[0].Data.Fields;

        // Canonical descriptor name, not the camelCase wire key `wordCount`.
        fields.Should().ContainKey("WordCount");
        fields["WordCount"].KindCase.Should().Be(Value.KindOneofCase.NumberValue);
        fields["WordCount"].NumberValue.Should().Be(100);

        // Same type resolution, exercised through the uppercase-declared SqlType this time.
        fields.Should().ContainKey("ViewCount");
        fields["ViewCount"].KindCase.Should().Be(Value.KindOneofCase.NumberValue);
        fields["ViewCount"].NumberValue.Should().Be(250);

        // Identity field is emitted under the key column's own name ("Id"), not the literal wire
        // key "key" it was seeded under.
        fields.Should().ContainKey("Id");
    }

    [Fact]
    public async Task SearchSimilar_EmitsTheKeyTheConsumerActuallyWrote_ForAMultiWordColumn()
    {
        // The write side (IntelligenceStoreConsumer.BuildObjectPointPayload) and the read side
        // (ObjectSearchGrpcService's columnLookup) each derive the Qdrant payload key from the same
        // ColumnDescriptor.Name through the same ToCamelCase. Both halves are covered elsewhere in
        // this file and in IntelligenceStoreConsumerTests — but each of those seeds or asserts its
        // own hardcoded literal ("wordCount"), so nothing fails if one side's derivation changes.
        // This test removes the literal from the middle: it captures the payload the real consumer
        // produces and replays that dictionary verbatim into the collection SearchSimilar reads.
        // A multi-word column is the subject deliberately — a single-word one cannot distinguish
        // camelCase from a hypothetical snake_case or all-lower derivation.
        var collection = UniqueName();
        var baseSchema = SchemaFixtures.ArticleSchema();
        var schema = baseSchema with
        {
            CollectionName = collection,
            ScalarColumns  = [
                .. baseSchema.ScalarColumns,
                new ColumnDescriptor("DocId", "text", false),
                // Production's SchemaBuilder adds the tenant discriminator as a real scalar column,
                // which is how it reaches the point payload at all; the shared fixture omits it.
                new ColumnDescriptor("TenantId", "text", false)],
            // Dropped so the object-level point is the only UpsertNamedAsync the capture can see —
            // the chunk path writes through the same method.
            ChunkFields    = []
        };
        await _registry.RegisterAsync(schema);

        var vec = new float[768];
        vec[0] = 1f;
        // This test drives both halves of the round trip on the same substitute: the real
        // consumer below embeds the write side via EmbedDocumentAsync, and the read side's
        // SearchSimilar call further down embeds the query via EmbedQueryAsync.
        _embedding.EmbedDocumentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(vec);
        _embedding.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(vec);

        // ── write side: run the real consumer, capture what it hands the vector store ──
        var vectorWrite = Substitute.For<IVectorWriteService>();
        string? capturedCollection = null;
        IReadOnlyDictionary<string, float[]>? capturedVectors = null;
        IReadOnlyDictionary<string, object>? capturedPayload = null;
        vectorWrite
            .UpsertNamedAsync(
                Arg.Do<string>(c => capturedCollection = c),
                Arg.Any<ulong>(),
                Arg.Do<IReadOnlyDictionary<string, float[]>>(v => capturedVectors = v),
                Arg.Do<IReadOnlyDictionary<string, object>?>(p => capturedPayload = p))
            .Returns(Task.CompletedTask);

        var entities = Substitute.For<IEntityRepository>();
        entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
                .Returns($$"""{"TenantId":"{{TestTenant}}"}""");

        var vectorSchema = Substitute.For<IVectorSchemaManager>();
        vectorSchema.ApplyCollectionAsync(Arg.Any<CollectionSchema>()).Returns(Task.CompletedTask);

        var consumer = new IntelligenceStoreConsumer(
            Substitute.For<IEventConsumer>(),
            vectorSchema,
            vectorWrite,
            _resolver,
            _registry,
            entities,
            new DocumentRenderer(_registry, entities),
            _tenantScope,
            Substitute.For<IEnrichmentService>(),
            Options.Create(new EnrichmentServiceOptions()),
            NullLogger<IntelligenceStoreConsumer>.Instance);

        const string docId = "corpus-doc-42";
        var ev = new EntityEvent(
            EventType:     EntityEventType.Created,
            TypeName:      "Article",
            Key:           Guid.NewGuid().ToString(),
            PayloadJson:   $$"""{"Title":"Great Title","DocId":"{{docId}}","TenantId":"{{TestTenant}}"}""",
            TraceId:       "trace-roundtrip",
            SchemaVersion: "1",
            OccurredAt:    DateTimeOffset.UtcNow,
            TargetStores:  StoreTarget.Intelligence);

        await consumer.HandleAsync(
            ev.Key,
            JsonSerializer.Serialize(
                ev,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
            CancellationToken.None);

        capturedPayload.Should().NotBeNull(
            "the consumer must write an object-level point for a schema with a vector field");
        capturedCollection.Should().NotBeNull();

        // ── replay: the captured dictionary goes into Qdrant untouched ──
        await _mgr.ApplyCollectionAsync(new CollectionSchema(
            capturedCollection!, [new NamedVector("title_vector", 768)], []));
        await _vector.UpsertNamedAsync(capturedCollection!, 1, capturedVectors!, capturedPayload!);

        // ── read side: the real RPC over that same point ──
        var (writer, written) = MakeStream<SearchResponse>();
        await BuildSut().SearchSimilar(
            new SearchSimilarRequest { TypeName = "Article", Property = "Title", Query = "q", TopK = 10 },
            writer,
            TestServerCallContext.Create());

        written.Should().ContainSingle();
        var fields = written[0].Data.Fields;

        // The point of the whole test: the canonical descriptor name reaches the client, and the
        // value is the one that went in — with no literal wire key written down anywhere above.
        fields.Should().ContainKey("DocId");
        fields["DocId"].StringValue.Should().Be(docId);
    }

    [Fact]
    public async Task SearchChunks_WithPkEqualsFilter_ReturnsOnlyThatParentsChunks()
    {
        var collection = UniqueName();
        var schema = SchemaFixtures.ArticleSchema() with { CollectionName = collection };
        await _registry.RegisterAsync(schema);

        var chunksCollection = _tenantScope
            .ResolveCollectionName(
                collection,
                TestTenant,
                isChunks: true);
        await _mgr.ApplyCollectionAsync(
            new CollectionSchema(
                chunksCollection,
                [new NamedVector("body_vector", 4)],
                [new PayloadIndex("parent_id", PayloadIndexKind.Keyword)]));

        var vec = new float[] { 0.1f, 0.2f, 0.3f, 0.4f };
        _embedding
            .EmbedQueryAsync(
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(vec);

        await _vector.UpsertNamedAsync(chunksCollection, 1,
            new Dictionary<string, float[]> { ["body_vector"] = vec },
            new Dictionary<string, object> { ["text"] = "chunk from parent A", ["parent_id"] = "parent-a", ["tenantId"] = "test-tenant" });
        await _vector.UpsertNamedAsync(chunksCollection, 2,
            new Dictionary<string, float[]> { ["body_vector"] = vec },
            new Dictionary<string, object> { ["text"] = "chunk from parent B", ["parent_id"] = "parent-b", ["tenantId"] = "test-tenant" });

        var sut = BuildSut();
        var request = new SearchChunksRequest { TypeName = "Article", Property = "Body", Query = "q", TopK = 10 };
        request.Filter.Add(new SearchClause
        {
            Property = "Id", Operator = SearchOperator.Equals,
            Value = new SearchValue { StringVal = "parent-a" }, ClauseType = SearchClauseType.Filter
        });

        var (writer, written) = MakeStream<ChunkSearchResponse>();
        await sut.SearchChunks(request, writer, TestServerCallContext.Create());

        written.Should().ContainSingle();
        written[0].ParentKey.Should().Be("parent-a");
    }

    // ── Popularity signal (Task 6) ──────────────────────────────────────────

    [Fact]
    public async Task SearchSimilar_PromotesLowerCosineButMorePopularCandidate_WhenWPopularityRaised()
    {
        var collection = UniqueName();
        var schema = SchemaFixtures.ArticleSchema() with { CollectionName = collection };
        await _registry.RegisterAsync(schema);
        var physicalCollection = _tenantScope.ResolveCollectionName(collection, TestTenant, isChunks: false);
        await _mgr.ApplyCollectionAsync(new CollectionSchema(
            physicalCollection, [new NamedVector("title_vector", 4)], []));

        var query = new float[] { 1f, 0f, 0f, 0f };
        _embedding.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(query);

        // Point 1: perfectly cosine-aligned with the query (highest possible base score) but has
        // zero comments — commentsCount=0 is a REAL signal (Popularity=0.0, not absent), so this
        // candidate is diluted through the same weighted-average branch as point 2 rather than
        // keeping its raw 1.0 base score via the no-signals-present short circuit.
        await _vector.UpsertNamedAsync(physicalCollection, 1,
            new Dictionary<string, float[]> { ["title_vector"] = query },
            new Dictionary<string, object>
            {
                ["title"] = "HighSimUnpopular", ["commentsCount"] = 0L, ["tenantId"] = TestTenant
            });
        // Point 2: orthogonal to the query (base score ~0) but very popular.
        await _vector.UpsertNamedAsync(physicalCollection, 2,
            new Dictionary<string, float[]> { ["title_vector"] = new float[] { 0f, 1f, 0f, 0f } },
            new Dictionary<string, object>
            {
                ["title"] = "LowSimPopular", ["commentsCount"] = 1000L, ["tenantId"] = TestTenant
            });

        var popularity = new PopularitySignalOptions
        {
            Signals         = [new PopularitySignalEntry("Article", "Comments")],
            SaturationPoint = 1.0
        };
        var sut = BuildSut(popularity, wPopularity: 5.0);
        var request = new SearchSimilarRequest { TypeName = "Article", Property = "Title", Query = "q", TopK = 2 };

        var (writer, written) = MakeStream<SearchResponse>();
        await sut.SearchSimilar(request, writer, TestServerCallContext.Create());

        written.Should().HaveCount(2);
        // Without the popularity signal, cosine order alone would put HighSimUnpopular first —
        // WPopularity=5.0 against WBase=0.45 flips the fused order.
        written[0].Data.Fields["Title"].StringValue.Should().Be("LowSimPopular");
        written[1].Data.Fields["Title"].StringValue.Should().Be("HighSimUnpopular");
    }

    [Fact]
    public async Task SearchChunks_ReflectsPopularitySignal_ForConfiguredType()
    {
        // Closes finding 2.2: SearchChunks must apply the same batched-by-parent popularity
        // lookup SearchSimilar's object-vector path applies directly.
        var collection = UniqueName();
        var schema = SchemaFixtures.ArticleSchema() with { CollectionName = collection };
        await _registry.RegisterAsync(schema);

        var objectCollection = _tenantScope.ResolveCollectionName(collection, TestTenant, isChunks: false);
        var chunksCollection = _tenantScope.ResolveCollectionName(collection, TestTenant, isChunks: true);
        await _mgr.ApplyCollectionAsync(new CollectionSchema(
            objectCollection, [new NamedVector("title_vector", 4)], []));
        await _mgr.ApplyCollectionAsync(new CollectionSchema(
            chunksCollection, [new NamedVector("body_vector", 4)],
            [new PayloadIndex("parent_id", PayloadIndexKind.Keyword)]));

        var query = new float[] { 1f, 0f, 0f, 0f };
        _embedding.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(query);

        const string similarParent = "parent-similar";
        const string popularParent = "parent-popular";
        var similarParentId = IntelligenceStoreConsumer.KeyToUlong(similarParent);
        var popularParentId = IntelligenceStoreConsumer.KeyToUlong(popularParent);

        // Object-level points carry the popularity payload the chunk path batches by parent id —
        // the chunk path never reads title_vector, only the "commentsCount" payload field.
        await _vector.UpsertNamedAsync(objectCollection, similarParentId,
            new Dictionary<string, float[]> { ["title_vector"] = query },
            new Dictionary<string, object> { ["commentsCount"] = 0L, ["tenantId"] = TestTenant });
        await _vector.UpsertNamedAsync(objectCollection, popularParentId,
            new Dictionary<string, float[]> { ["title_vector"] = query },
            new Dictionary<string, object> { ["commentsCount"] = 1000L, ["tenantId"] = TestTenant });

        // Chunk 1 (parent-similar) matches the query exactly; chunk 2 (parent-popular) is
        // orthogonal — cosine order alone would rank parent-similar first.
        await _vector.UpsertNamedAsync(chunksCollection, 1,
            new Dictionary<string, float[]> { ["body_vector"] = query },
            new Dictionary<string, object>
            {
                ["text"] = "similar chunk", ["parent_id"] = similarParent, ["tenantId"] = TestTenant
            });
        await _vector.UpsertNamedAsync(chunksCollection, 2,
            new Dictionary<string, float[]> { ["body_vector"] = new float[] { 0f, 1f, 0f, 0f } },
            new Dictionary<string, object>
            {
                ["text"] = "popular chunk", ["parent_id"] = popularParent, ["tenantId"] = TestTenant
            });

        var popularity = new PopularitySignalOptions
        {
            Signals         = [new PopularitySignalEntry("Article", "Comments")],
            SaturationPoint = 1.0
        };
        var sut = BuildSut(popularity, wPopularity: 5.0);
        var request = new SearchChunksRequest { TypeName = "Article", Property = "Body", Query = "q", TopK = 2 };

        var (writer, written) = MakeStream<ChunkSearchResponse>();
        await sut.SearchChunks(request, writer, TestServerCallContext.Create());

        written.Should().HaveCount(2);
        written[0].ParentKey.Should().Be(popularParent);
        written[1].ParentKey.Should().Be(similarParent);
    }

    [Fact]
    public async Task SearchSimilar_UnconfiguredType_OrdersIdenticallyToBaseline()
    {
        // No PopularitySignalEntry names "Article" — even with WPopularity raised, Popularity
        // stays null for every candidate of this type, so ResultReranker takes its
        // no-other-signal-present short circuit and the fused score equals the base score
        // EXACTLY (see ResultReranker.cs), preserving today's cosine-only ordering bit for bit.
        var collection = UniqueName();
        var schema = SchemaFixtures.ArticleSchema() with { CollectionName = collection };
        await _registry.RegisterAsync(schema);
        var physicalCollection = _tenantScope.ResolveCollectionName(collection, TestTenant, isChunks: false);
        await _mgr.ApplyCollectionAsync(new CollectionSchema(
            physicalCollection, [new NamedVector("title_vector", 4)], []));

        var query = new float[] { 1f, 0f, 0f, 0f };
        _embedding.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(query);

        await _vector.UpsertNamedAsync(physicalCollection, 1,
            new Dictionary<string, float[]> { ["title_vector"] = query },
            new Dictionary<string, object> { ["title"] = "HighSim", ["tenantId"] = TestTenant });
        // A high "commentsCount" on the orthogonal candidate would flip the order if the type
        // were configured (see the promotion test above) — it must NOT here.
        await _vector.UpsertNamedAsync(physicalCollection, 2,
            new Dictionary<string, float[]> { ["title_vector"] = new float[] { 0f, 1f, 0f, 0f } },
            new Dictionary<string, object>
            {
                ["title"] = "LowSim", ["commentsCount"] = 1000L, ["tenantId"] = TestTenant
            });

        // Default PopularitySignalOptions() has an empty Signals list — "Article" unconfigured.
        var sut = BuildSut(wPopularity: 5.0);
        var request = new SearchSimilarRequest { TypeName = "Article", Property = "Title", Query = "q", TopK = 2 };

        var (writer, written) = MakeStream<SearchResponse>();
        await sut.SearchSimilar(request, writer, TestServerCallContext.Create());

        written.Should().HaveCount(2);
        written[0].Data.Fields["Title"].StringValue.Should().Be("HighSim");
        written[1].Data.Fields["Title"].StringValue.Should().Be("LowSim");
    }
}
