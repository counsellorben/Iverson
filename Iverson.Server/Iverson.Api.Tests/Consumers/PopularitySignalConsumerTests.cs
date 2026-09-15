using System.Text.Json;
using FluentAssertions;
using Grpc.Core;
using Iverson.Api.Consumers;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Events;
using Iverson.Sql;
using Iverson.StarRocks;
using Iverson.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

// Same ambiguity as PopularitySignalConsumer.cs itself — see the aliases there for why.
using EngagementAggResult = Iverson.StarRocks.AggregationResult;
using SrAggBucket = Iverson.StarRocks.AggregationBucket;
using SchemaRelationDescriptor = Iverson.Api.Schema.RelationDescriptor;
using SchemaRelationKind = Iverson.Api.Schema.RelationKind;

namespace Iverson.Api.Tests.Consumers;

public class PopularitySignalConsumerTests
{
    private readonly IEventConsumer _consumer = Substitute.For<IEventConsumer>();
    private readonly IRecordStoreQueryExecutor _sql = Substitute.For<IRecordStoreQueryExecutor>();
    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();
    private readonly IEngagementStoreSearchService _search = Substitute.For<IEngagementStoreSearchService>();
    private readonly IVectorWriteService _vector = Substitute.For<IVectorWriteService>();
    private readonly IntelligenceTenantScope _tenantScope = new("test-signing-key-0123456789abcdef");
    private readonly SchemaRegistry _registry;

    private const string TenantA = "tenant-a";

    private static readonly string ArticleId  = "11111111-0000-0000-0000-000000000001";
    private static readonly string Article2Id = "11111111-0000-0000-0000-000000000002";
    private static readonly string CommentId  = "22222222-0000-0000-0000-000000000001";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public PopularitySignalConsumerTests()
    {
        _registry = new SchemaRegistry(new SchemaRegistryRepository(_sql), NullLogger<SchemaRegistry>.Instance);
    }

    private string Serialize(EntityEvent ev) => JsonSerializer.Serialize(ev, JsonOptions);

    private static PopularitySignalOptions OptionsWith(string parentType, string relation) => new()
    {
        Signals = [new PopularitySignalEntry(parentType, relation)]
    };

    private PopularitySignalConsumer BuildSut(PopularitySignalOptions options)
    {
        var updater = new PopularitySignalUpdater(
            _search, _vector, _tenantScope, NullLogger<PopularitySignalUpdater>.Instance);
        return new PopularitySignalConsumer(
            _consumer, _registry, _entities, Options.Create(options), updater,
            NullLogger<PopularitySignalConsumer>.Instance);
    }

    // UpdateAsync's outcome is not observable through DispatchAsync (which discards it), so the
    // outcome tests drive the updater directly. Same construction as BuildSut's.
    private PopularitySignalUpdater BuildUpdater() =>
        new(_search, _vector, _tenantScope, NullLogger<PopularitySignalUpdater>.Instance);

    // ── Schema fixtures ──────────────────────────────────────────────────────
    // Article: the parent side of a configured "Comments" OneToMany signal (FK lives on
    // Comment.ArticleId). Needs a vector field and a CollectionName — both are load-bearing:
    // ValidateAtStartup requires the former in production, and UpdateAsync dereferences the
    // latter (`parentSchema.CollectionName!`) to resolve the Qdrant collection to patch.
    private static SchemaDescriptor ArticleSchema() => new()
    {
        TypeName       = "Article",
        TableName      = "articles",
        CollectionName = "articles",
        KeyColumn      = new ColumnDescriptor("Id", "UUID", false),
        ScalarColumns  = [],
        FkColumns      = [],
        VectorFields   = [new VectorDescriptor("Title", 768, "nomic-embed-text")],
        ChunkFields    = [],
        Relations      = [new SchemaRelationDescriptor("Comments", SchemaRelationKind.OneToMany, "Comment", "ArticleId")],
        TenantColumn   = "TenantId",
    };

    // popularitySignalColumn: null reproduces a child with no marked timestamp column (the write
    // path's series-less branch); a column name reproduces one Task 2's client/server marking
    // flagged, driving the DateHistogram aggregation this file's tests exercise.
    private static SchemaDescriptor CommentSchema(string? popularitySignalColumn = null) => new()
    {
        TypeName      = "Comment",
        TableName     = "comments",
        KeyColumn     = new ColumnDescriptor("Id", "UUID", false),
        ScalarColumns =
        [
            new ColumnDescriptor("Body", "TEXT", false), new ColumnDescriptor("ArticleId", "UUID", true),
            new ColumnDescriptor("PostedAt", "TIMESTAMPTZ", false)
        ],
        FkColumns              = [],
        VectorFields           = [],
        ChunkFields            = [],
        Relations              = [],
        TenantColumn           = "TenantId",
        PopularitySignalColumn = popularitySignalColumn,
    };

    private static EntityEvent MakeEvent(
        EntityEventType type, string typeName, string key, string payload, string? priorPayload = null) =>
        new(
            EventType:        type,
            TypeName:         typeName,
            Key:              key,
            PayloadJson:      payload,
            TraceId:          "trace-1",
            SchemaVersion:    "1",
            OccurredAt:       DateTimeOffset.UtcNow,
            TargetStores:     StoreTarget.All,
            PriorPayloadJson: priorPayload);

    // The Count and DateHistogram aggregations go through the same AggregateAsync method, so these
    // stubs discriminate on the spec's Kind rather than Arg.Any<AggregationDescriptor>() — otherwise
    // a test that configures both would have the later setup win for every call regardless of kind.
    private void StubCount(long count) =>
        _search.AggregateAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(),
                Arg.Is<AggregationDescriptor>(a => a.Kind == AggregationKind.Count),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns((EngagementAggResult?)new EngagementAggResult("count", AggregationKind.Count, MetricValue: count));

    private void StubHistogram(IReadOnlyList<SrAggBucket> buckets) =>
        _search.AggregateAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(),
                Arg.Is<AggregationDescriptor>(a => a.Kind == AggregationKind.DateHistogram),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns((EngagementAggResult?)new EngagementAggResult("buckets", AggregationKind.DateHistogram, Buckets: buckets));

    private void StubHistogramThrows(Exception ex) =>
        _search.AggregateAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(),
                Arg.Is<AggregationDescriptor>(a => a.Kind == AggregationKind.DateHistogram),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns(Task.FromException<EngagementAggResult?>(ex));

    // ── Created/Updated/Deleted all trigger AggregateAsync with a tenant-scoped filter ──

    [Theory]
    [InlineData(EntityEventType.Created)]
    [InlineData(EntityEventType.Updated)]
    [InlineData(EntityEventType.Deleted)]
    public async Task Dispatch_AllEventTypes_TriggersTenantScopedAggregateAndSetsPayload(EntityEventType eventType)
    {
        await _registry.RegisterAsync(ArticleSchema());
        await _registry.RegisterAsync(CommentSchema());
        StubCount(3);

        var payload = $$"""{"Id":"{{CommentId}}","Body":"hi","ArticleId":"{{ArticleId}}","TenantId":"{{TenantA}}"}""";
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), CommentId, Arg.Any<EntityAccess>()).Returns(payload);

        var ev = MakeEvent(eventType, "Comment", CommentId, payload);
        var sut = BuildSut(OptionsWith("Article", "Comments"));

        await sut.DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _search.Received(1).AggregateAsync(
            Arg.Any<EngagementQuerySchema>(),
            Arg.Is<SearchQuery?>(q =>
                q != null && q.Clauses.Count == 1 &&
                q.Clauses[0].Property == "ArticleId" &&
                q.Clauses[0].Operator == SearchOperator.Equals &&
                q.Clauses[0].ClauseType == SearchClauseType.Filter &&
                q.Clauses[0].Value.StringVal == ArticleId),
            Arg.Any<AggregationDescriptor>(),
            Arg.Any<SearchQuery?>(),
            Arg.Any<IReadOnlyList<JoinSpec>?>(),
            Arg.Any<Func<string, EngagementQuerySchema?>?>(),
            Arg.Is<IReadOnlyDictionary<string, AuthorizationConstraint>?>(a =>
                a != null &&
                a.ContainsKey("Comment") &&
                a["Comment"].TenantColumn == "TenantId" &&
                a["Comment"].TenantValue == TenantA));

        var expectedPointId = IntelligenceStoreConsumer.KeyToUlong(ArticleId);
        await _vector.Received(1).SetPayloadAsync(
            _tenantScope.ResolveCollectionName("articles", TenantA, isChunks: false),
            expectedPointId,
            Arg.Is<IReadOnlyDictionary<string, object>>(p =>
                p.Count == 2 &&
                p.ContainsKey("commentsCount") && (long)p["commentsCount"] == 3L &&
                p.ContainsKey("commentsCountBuckets") && (string)p["commentsCountBuckets"] == ""));
    }

    // ── FK reassignment updates BOTH the old and new parent ─────────────────

    [Fact]
    public async Task Dispatch_CommentReparented_UpdatesBothOldAndNewParent()
    {
        await _registry.RegisterAsync(ArticleSchema());
        await _registry.RegisterAsync(CommentSchema());
        StubCount(1);

        var newPayload   = $$"""{"Id":"{{CommentId}}","Body":"hi","ArticleId":"{{Article2Id}}","TenantId":"{{TenantA}}"}""";
        var priorPayload = $$"""{"Id":"{{CommentId}}","Body":"hi","ArticleId":"{{ArticleId}}","TenantId":"{{TenantA}}"}""";
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), CommentId, Arg.Any<EntityAccess>()).Returns(newPayload);

        var ev = MakeEvent(EntityEventType.Updated, "Comment", CommentId, newPayload, priorPayload: priorPayload);
        var sut = BuildSut(OptionsWith("Article", "Comments"));

        await sut.DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _vector.Received(1).SetPayloadAsync(
            _tenantScope.ResolveCollectionName("articles", TenantA, isChunks: false), IntelligenceStoreConsumer.KeyToUlong(Article2Id),
            Arg.Any<IReadOnlyDictionary<string, object>>());
        await _vector.Received(1).SetPayloadAsync(
            _tenantScope.ResolveCollectionName("articles", TenantA, isChunks: false), IntelligenceStoreConsumer.KeyToUlong(ArticleId),
            Arg.Any<IReadOnlyDictionary<string, object>>());
    }

    // ── The bucket series ────────────────────────────────────────────────────

    [Fact]
    public async Task Dispatch_ChildHasMarkedColumn_WritesEncodedSeries()
    {
        await _registry.RegisterAsync(ArticleSchema());
        await _registry.RegisterAsync(CommentSchema("PostedAt"));
        StubCount(2);
        StubHistogram([new SrAggBucket("2026-07", 1), new SrAggBucket("2026-08", 1)]);

        var payload = $$"""{"Id":"{{CommentId}}","Body":"hi","ArticleId":"{{ArticleId}}","TenantId":"{{TenantA}}"}""";
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), CommentId, Arg.Any<EntityAccess>()).Returns(payload);

        var ev = MakeEvent(EntityEventType.Created, "Comment", CommentId, payload);
        var sut = BuildSut(OptionsWith("Article", "Comments"));

        await sut.DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);

        // The only thing that scopes this histogram to one tenant is the authz argument threaded
        // through to AggregateAsync — EngagementRepository.AggregateAsync branches on
        // `authz is null` into an UNSCOPED query with no SET ROLE and no tenant predicate. Assert
        // the same tenant predicate the Count call already carries (see
        // Dispatch_AllEventTypes_TriggersTenantScopedAggregateAndSetsPayload above), so losing the
        // authz argument on the DateHistogram call specifically would fail this test.
        await _search.Received(1).AggregateAsync(
            Arg.Any<EngagementQuerySchema>(),
            Arg.Any<SearchQuery?>(),
            Arg.Is<AggregationDescriptor>(a => a.Kind == AggregationKind.DateHistogram),
            Arg.Any<SearchQuery?>(),
            Arg.Any<IReadOnlyList<JoinSpec>?>(),
            Arg.Any<Func<string, EngagementQuerySchema?>?>(),
            Arg.Is<IReadOnlyDictionary<string, AuthorizationConstraint>?>(a =>
                a != null &&
                a.ContainsKey("Comment") &&
                a["Comment"].TenantColumn == "TenantId" &&
                a["Comment"].TenantValue == TenantA));

        await _vector.Received(1).SetPayloadAsync(
            _tenantScope.ResolveCollectionName("articles", TenantA, isChunks: false),
            IntelligenceStoreConsumer.KeyToUlong(ArticleId),
            Arg.Is<IReadOnlyDictionary<string, object>>(p =>
                p.Count == 2 &&
                (long)p["commentsCount"] == 2L &&
                (string)p["commentsCountBuckets"] == "2026-07:1;2026-08:1"));
    }

    [Fact]
    public async Task Dispatch_ChildHasNoMarkedColumn_WritesEmptySeries()
    {
        await _registry.RegisterAsync(ArticleSchema());
        await _registry.RegisterAsync(CommentSchema()); // no PopularitySignalColumn
        StubCount(2);

        var payload = $$"""{"Id":"{{CommentId}}","Body":"hi","ArticleId":"{{ArticleId}}","TenantId":"{{TenantA}}"}""";
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), CommentId, Arg.Any<EntityAccess>()).Returns(payload);

        var ev = MakeEvent(EntityEventType.Created, "Comment", CommentId, payload);
        var sut = BuildSut(OptionsWith("Article", "Comments"));

        await sut.DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);

        // No PopularitySignalColumn means the histogram must never even be issued.
        await _search.DidNotReceive().AggregateAsync(
            Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(),
            Arg.Is<AggregationDescriptor>(a => a.Kind == AggregationKind.DateHistogram),
            Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
            Arg.Any<Func<string, EngagementQuerySchema?>?>(),
            Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>());

        await _vector.Received(1).SetPayloadAsync(
            _tenantScope.ResolveCollectionName("articles", TenantA, isChunks: false),
            IntelligenceStoreConsumer.KeyToUlong(ArticleId),
            Arg.Is<IReadOnlyDictionary<string, object>>(p =>
                p.Count == 2 &&
                (long)p["commentsCount"] == 2L &&
                (string)p["commentsCountBuckets"] == ""));
    }

    [Fact]
    public async Task Dispatch_HistogramFails_StillWritesCountWithEmptySeries()
    {
        await _registry.RegisterAsync(ArticleSchema());
        await _registry.RegisterAsync(CommentSchema("PostedAt"));
        StubCount(5);
        StubHistogramThrows(new InvalidOperationException("boom"));

        var payload = $$"""{"Id":"{{CommentId}}","Body":"hi","ArticleId":"{{ArticleId}}","TenantId":"{{TenantA}}"}""";
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), CommentId, Arg.Any<EntityAccess>()).Returns(payload);

        var ev = MakeEvent(EntityEventType.Created, "Comment", CommentId, payload);
        var sut = BuildSut(OptionsWith("Article", "Comments"));

        var act = () => sut.DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);
        await act.Should().NotThrowAsync();

        await _vector.Received(1).SetPayloadAsync(
            _tenantScope.ResolveCollectionName("articles", TenantA, isChunks: false),
            IntelligenceStoreConsumer.KeyToUlong(ArticleId),
            Arg.Is<IReadOnlyDictionary<string, object>>(p =>
                p.Count == 2 &&
                (long)p["commentsCount"] == 5L &&
                (string)p["commentsCountBuckets"] == ""));
    }

    [Fact]
    public async Task Dispatch_SeventyBuckets_TruncatesToMostRecent60()
    {
        await _registry.RegisterAsync(ArticleSchema());
        await _registry.RegisterAsync(CommentSchema("PostedAt"));
        StubCount(70);

        // Ascending by key, as SQL's ORDER BY bucket_key guarantees — bucket 0 is the oldest,
        // bucket 69 the newest. TakeLast(60) must keep buckets 10..69 and drop 0..9.
        var buckets = Enumerable.Range(0, 70)
            .Select(i => new SrAggBucket($"2020-{i:D2}", 1))
            .ToList();
        StubHistogram(buckets);

        var payload = $$"""{"Id":"{{CommentId}}","Body":"hi","ArticleId":"{{ArticleId}}","TenantId":"{{TenantA}}"}""";
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), CommentId, Arg.Any<EntityAccess>()).Returns(payload);

        var ev = MakeEvent(EntityEventType.Created, "Comment", CommentId, payload);
        var sut = BuildSut(OptionsWith("Article", "Comments"));

        await sut.DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);

        var expectedSeries = string.Join(";", buckets.TakeLast(60).Select(b => $"{b.Key}:{b.DocCount}"));

        await _vector.Received(1).SetPayloadAsync(
            _tenantScope.ResolveCollectionName("articles", TenantA, isChunks: false),
            IntelligenceStoreConsumer.KeyToUlong(ArticleId),
            Arg.Is<IReadOnlyDictionary<string, object>>(p =>
                (string)p["commentsCountBuckets"] == expectedSeries &&
                !((string)p["commentsCountBuckets"]).Contains("2020-00:1") &&
                ((string)p["commentsCountBuckets"]).Contains("2020-69:1")));
    }

    // ── Two documented degrade cases ────────────────────────────────────────

    [Fact]
    public async Task Dispatch_AggregateAsyncReturnsNull_SkipsWithoutThrowing()
    {
        await _registry.RegisterAsync(ArticleSchema());
        await _registry.RegisterAsync(CommentSchema());
        _search.AggregateAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns((EngagementAggResult?)null);

        var payload = $$"""{"Id":"{{CommentId}}","Body":"hi","ArticleId":"{{ArticleId}}","TenantId":"{{TenantA}}"}""";
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), CommentId, Arg.Any<EntityAccess>()).Returns(payload);

        var ev = MakeEvent(EntityEventType.Created, "Comment", CommentId, payload);
        var sut = BuildSut(OptionsWith("Article", "Comments"));

        var act = () => sut.DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);
        await act.Should().NotThrowAsync();

        await _vector.DidNotReceiveWithAnyArgs().SetPayloadAsync(
            default!, default, Arg.Any<IReadOnlyDictionary<string, object>>());
    }

    [Fact]
    public async Task Dispatch_SetPayloadAsyncThrowsQdrantNotFound_SkipsWithoutThrowing()
    {
        await _registry.RegisterAsync(ArticleSchema());
        await _registry.RegisterAsync(CommentSchema());
        StubCount(1);
        _vector.SetPayloadAsync(
                Arg.Any<string>(), Arg.Any<ulong>(), Arg.Any<IReadOnlyDictionary<string, object>>())
            .Returns(Task.FromException(new RpcException(new Status(StatusCode.NotFound, "no such point"))));

        var payload = $$"""{"Id":"{{CommentId}}","Body":"hi","ArticleId":"{{ArticleId}}","TenantId":"{{TenantA}}"}""";
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), CommentId, Arg.Any<EntityAccess>()).Returns(payload);

        var ev = MakeEvent(EntityEventType.Created, "Comment", CommentId, payload);
        var sut = BuildSut(OptionsWith("Article", "Comments"));

        var act = () => sut.DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    // A non-NotFound RpcException is NOT one of the two documented degrade cases and must
    // propagate to DispatchAsync's own per-signal catch, which logs and moves on rather than
    // crashing the whole dispatch — but it must not be silently swallowed by UpdateAsync itself.
    [Fact]
    public async Task Dispatch_SetPayloadAsyncThrowsOtherRpcException_IsCaughtBySignalLevelHandler()
    {
        await _registry.RegisterAsync(ArticleSchema());
        await _registry.RegisterAsync(CommentSchema());
        StubCount(1);
        _vector.SetPayloadAsync(
                Arg.Any<string>(), Arg.Any<ulong>(), Arg.Any<IReadOnlyDictionary<string, object>>())
            .Returns(Task.FromException(new RpcException(new Status(StatusCode.Unavailable, "down"))));

        var payload = $$"""{"Id":"{{CommentId}}","Body":"hi","ArticleId":"{{ArticleId}}","TenantId":"{{TenantA}}"}""";
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), CommentId, Arg.Any<EntityAccess>()).Returns(payload);

        var ev = MakeEvent(EntityEventType.Created, "Comment", CommentId, payload);
        var sut = BuildSut(OptionsWith("Article", "Comments"));

        var act = () => sut.DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);
        await act.Should().NotThrowAsync("DispatchAsync isolates per-signal failures the same way DocumentRerenderConsumer isolates per-dependent failures");
    }

    // ── Unresolvable tenant skips without calling AggregateAsync at all ────

    [Fact]
    public async Task Dispatch_AuthoritativeRowGone_SkipsWithoutCallingAggregate()
    {
        await _registry.RegisterAsync(ArticleSchema());
        await _registry.RegisterAsync(CommentSchema());

        // The Comment row was deleted between the Created event being published and this
        // consumer reading it, so tenant re-derivation finds nothing — mirrors
        // DocumentRerenderConsumerTests.Dispatch_AuthoritativeRowGone_EnqueuesNothing.
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), CommentId, Arg.Any<EntityAccess>()).Returns((string?)null);

        var payload = $$"""{"Id":"{{CommentId}}","Body":"hi","ArticleId":"{{ArticleId}}","TenantId":"{{TenantA}}"}""";
        var ev = MakeEvent(EntityEventType.Created, "Comment", CommentId, payload);
        var sut = BuildSut(OptionsWith("Article", "Comments"));

        await sut.DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _search.DidNotReceiveWithAnyArgs().AggregateAsync(
            default!, default, default!, default, default, default, default);
        await _vector.DidNotReceiveWithAnyArgs().SetPayloadAsync(
            default!, default, Arg.Any<IReadOnlyDictionary<string, object>>());
    }

    [Fact]
    public async Task Dispatch_AuthoritativeRowHasNoTenantValue_ThrowsPoisonWithoutCallingAggregate()
    {
        await _registry.RegisterAsync(ArticleSchema());
        await _registry.RegisterAsync(CommentSchema());

        // Row present, tenant column absent from it — an invariant violation, so it dead-letters.
        var payload = $$"""{"Id":"{{CommentId}}","Body":"hi","ArticleId":"{{ArticleId}}"}""";
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), CommentId, Arg.Any<EntityAccess>()).Returns(payload);

        var ev = MakeEvent(EntityEventType.Created, "Comment", CommentId, payload);
        var sut = BuildSut(OptionsWith("Article", "Comments"));

        var act = () => sut.DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await act.Should().ThrowAsync<PoisonMessageException>();
        await _search.DidNotReceiveWithAnyArgs().AggregateAsync(
            default!, default, default!, default, default, default, default);
    }

    // ── Unrelated event type is a no-op ─────────────────────────────────────

    [Fact]
    public async Task Dispatch_UnrelatedEventType_IsNoOp()
    {
        await _registry.RegisterAsync(ArticleSchema());
        await _registry.RegisterAsync(CommentSchema());

        // "Author" is not the related type of any configured signal (the only configured
        // signal's child is "Comment"), so this must short-circuit before any tenant
        // resolution or downstream call at all.
        var ev = MakeEvent(EntityEventType.Updated, "Author", "33333333-0000-0000-0000-000000000001",
            """{"Id":"33333333-0000-0000-0000-000000000001","Name":"Ada"}""");
        var sut = BuildSut(OptionsWith("Article", "Comments"));

        await sut.DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await _entities.DidNotReceiveWithAnyArgs().FetchByKeyAsync(default!, default!, default!);
        await _search.DidNotReceiveWithAnyArgs().AggregateAsync(
            default!, default, default!, default, default, default, default);
        await _vector.DidNotReceiveWithAnyArgs().SetPayloadAsync(
            default!, default, Arg.Any<IReadOnlyDictionary<string, object>>());
    }

    // ── CSR #14: poison-message guard on the 3 previously-unguarded JsonDocument.Parse calls ──
    // Kafka carries no schema enforcement — a malformed payload is a poison message, not a
    // transient fault, and must dead-letter (PoisonMessageException) rather than crash-loop this
    // consumer's whole group by throwing an unguarded JsonException up through ConsumerResilience.

    [Fact]
    public async Task Dispatch_MalformedCurrentPayloadJson_ThrowsPoisonMessageException()
    {
        await _registry.RegisterAsync(ArticleSchema());
        await _registry.RegisterAsync(CommentSchema());

        // Tenant resolution (Created) re-derives from the authoritative Postgres row, not from
        // ev.PayloadJson — stubbed well-formed so the malformed payload below is what's on trial.
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), CommentId, Arg.Any<EntityAccess>())
            .Returns($$"""{"Id":"{{CommentId}}","ArticleId":"{{ArticleId}}","TenantId":"{{TenantA}}"}""");

        var ev = MakeEvent(EntityEventType.Created, "Comment", CommentId, "NOT_VALID_JSON{{{");
        var sut = BuildSut(OptionsWith("Article", "Comments"));

        var act = () => sut.DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await act.Should().ThrowAsync<PoisonMessageException>();
    }

    [Fact]
    public async Task Dispatch_MalformedPriorPayloadJson_ThrowsPoisonMessageException()
    {
        await _registry.RegisterAsync(ArticleSchema());
        await _registry.RegisterAsync(CommentSchema());

        var payload = $$"""{"Id":"{{CommentId}}","ArticleId":"{{ArticleId}}","TenantId":"{{TenantA}}"}""";
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), CommentId, Arg.Any<EntityAccess>()).Returns(payload);

        var ev = MakeEvent(EntityEventType.Updated, "Comment", CommentId, payload, priorPayload: "NOT_VALID_JSON{{{");
        var sut = BuildSut(OptionsWith("Article", "Comments"));

        var act = () => sut.DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await act.Should().ThrowAsync<PoisonMessageException>();
    }

    [Fact]
    public async Task Dispatch_DeletedEventWithMalformedPayloadJson_ThrowsPoisonMessageException()
    {
        await _registry.RegisterAsync(ArticleSchema());
        await _registry.RegisterAsync(CommentSchema());

        // Deleted's tenant resolution reads straight out of the pre-delete payload snapshot — the
        // authoritative row is already gone by consumption time — so this is the third site
        // (ResolveTenantIdAsync's Deleted branch), reached before any entities lookup at all.
        var ev = MakeEvent(EntityEventType.Deleted, "Comment", CommentId, "NOT_VALID_JSON{{{");
        var sut = BuildSut(OptionsWith("Article", "Comments"));

        var act = () => sut.DispatchAsync(ev.Key, Serialize(ev), CancellationToken.None);

        await act.Should().ThrowAsync<PoisonMessageException>();
        await _entities.DidNotReceiveWithAnyArgs().FetchByKeyAsync(default!, default!, default!);
    }

    // ── UpdateAsync's outcome, driven directly (Task 2 consumes it to abort the sweep) ────

    // ── Outcome mapping: the aggregate's failure is swallowed internally (the log line stays),
    //    but it must now be REPORTED so the reconciliation sweep can count it. ──────────────
    [Fact]
    public async Task UpdateAsync_AggregateThrows_ReturnsFailed()
    {
        _search.AggregateAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns(Task.FromException<EngagementAggResult?>(new InvalidOperationException("starrocks down")));

        var outcome = await BuildUpdater().UpdateAsync(
            ArticleSchema(), new PopularitySignalEntry("Article", "Comments"), CommentSchema(),
            ArticleSchema().Relations[0], ArticleId, TenantA);

        outcome.Should().Be(PopularityUpdateOutcome.Failed);
    }

    // A null aggregate result is the DESIGNED outcome of the unprovisioned-tenant race — Skipped,
    // never Failed, or a sweep over an unprovisioned tenant would abandon itself.
    [Fact]
    public async Task UpdateAsync_AggregateReturnsNull_ReturnsSkipped()
    {
        _search.AggregateAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns((EngagementAggResult?)null);

        var outcome = await BuildUpdater().UpdateAsync(
            ArticleSchema(), new PopularitySignalEntry("Article", "Comments"), CommentSchema(),
            ArticleSchema().Relations[0], ArticleId, TenantA);

        outcome.Should().Be(PopularityUpdateOutcome.Skipped);
    }

    // The Qdrant point not existing yet is the other documented degrade case — also Skipped, and it
    // shares a terminal point with success, so a single trailing `return Updated` would mislabel it.
    [Fact]
    public async Task UpdateAsync_SetPayloadNotFound_ReturnsSkipped()
    {
        StubCount(3);
        _vector.SetPayloadAsync(
                Arg.Any<string>(), Arg.Any<ulong>(), Arg.Any<IReadOnlyDictionary<string, object>>())
            .Returns(Task.FromException(new RpcException(new Status(StatusCode.NotFound, "no point"))));

        var outcome = await BuildUpdater().UpdateAsync(
            ArticleSchema(), new PopularitySignalEntry("Article", "Comments"), CommentSchema(),
            ArticleSchema().Relations[0], ArticleId, TenantA);

        outcome.Should().Be(PopularityUpdateOutcome.Skipped);
    }

    // The histogram's catch has no return — the count was already written, so the outcome stays
    // Updated even though the series degrades to empty. This is the one row of the spec's exit-path
    // table with no outcome-level assertion: Dispatch_HistogramFails_StillWritesCountWithEmptySeries
    // above exercises the same path but goes through DispatchAsync, which discards the outcome.
    [Fact]
    public async Task UpdateAsync_HistogramThrows_ReturnsUpdated()
    {
        StubCount(5);
        StubHistogramThrows(new InvalidOperationException("boom"));

        var outcome = await BuildUpdater().UpdateAsync(
            ArticleSchema(), new PopularitySignalEntry("Article", "Comments"), CommentSchema("PostedAt"),
            ArticleSchema().Relations[0], ArticleId, TenantA);

        outcome.Should().Be(PopularityUpdateOutcome.Updated);
    }

    // ── Spec test 6: the fifth exit path. This is the ONLY assertion in the suite that a catch-all
    //    converting the propagating class into an outcome would fail — the two pre-existing tests
    //    that document the contract in comments assert only NotThrowAsync, which a swallowing
    //    catch-all also satisfies. StubCount is REQUIRED: without it the aggregate returns null and
    //    UpdateAsync returns Skipped before ever reaching the Qdrant write.
    [Fact]
    public async Task UpdateAsync_SetPayloadThrowsNonNotFound_Propagates()
    {
        StubCount(3);
        _vector.SetPayloadAsync(
                Arg.Any<string>(), Arg.Any<ulong>(), Arg.Any<IReadOnlyDictionary<string, object>>())
            .Returns(Task.FromException(new RpcException(new Status(StatusCode.Unavailable, "down"))));

        var act = () => BuildUpdater().UpdateAsync(
            ArticleSchema(), new PopularitySignalEntry("Article", "Comments"), CommentSchema(),
            ArticleSchema().Relations[0], ArticleId, TenantA);

        await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.Unavailable);
    }
}
