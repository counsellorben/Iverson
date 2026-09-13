using FluentAssertions;
using Iverson.Api.Consumers;
using Iverson.Api.Grpc;
using Iverson.Api.Reconciliation;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Sql;
using Iverson.StarRocks;
using Iverson.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

// Same ambiguity as PopularitySignalConsumer.cs / PopularitySignalConsumerTests.cs — see the
// aliases there for why.
using EngagementAggResult = Iverson.StarRocks.AggregationResult;
using SchemaRelationDescriptor = Iverson.Api.Schema.RelationDescriptor;
using SchemaRelationKind = Iverson.Api.Schema.RelationKind;

namespace Iverson.Api.Tests.Reconciliation;

public class PopularitySignalReconciliationWorkerTests
{
    private readonly IRecordStoreQueryExecutor _sql = Substitute.For<IRecordStoreQueryExecutor>();
    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();
    private readonly IEngagementStoreSearchService _search = Substitute.For<IEngagementStoreSearchService>();
    private readonly IVectorWriteService _vector = Substitute.For<IVectorWriteService>();
    private readonly IntelligenceTenantScope _tenantScope = new("test-signing-key-0123456789abcdef");
    private readonly SchemaRegistry _registry;

    private const string TenantA = "tenant-a";

    public PopularitySignalReconciliationWorkerTests()
    {
        _registry = new SchemaRegistry(new SchemaRegistryRepository(_sql), NullLogger<SchemaRegistry>.Instance);
    }

    // Same fixtures as PopularitySignalConsumerTests: Article is the parent side of a configured
    // "Comments" OneToMany signal (FK lives on Comment.ArticleId).
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

    private static SchemaDescriptor CommentSchema() => new()
    {
        TypeName      = "Comment",
        TableName     = "comments",
        KeyColumn     = new ColumnDescriptor("Id", "UUID", false),
        ScalarColumns = [new ColumnDescriptor("Body", "TEXT", false), new ColumnDescriptor("ArticleId", "UUID", true)],
        FkColumns     = [],
        VectorFields  = [],
        ChunkFields   = [],
        Relations     = [],
        TenantColumn  = "TenantId",
    };

    private static PopularitySignalEntry Signal() => new("Article", "Comments");

    private PopularitySignalReconciliationWorker BuildSut(PopularitySignalOptions? options = null)
    {
        var updater = new PopularitySignalUpdater(
            _search, _vector, _tenantScope, NullLogger<PopularitySignalUpdater>.Instance);
        return new PopularitySignalReconciliationWorker(
            Options.Create(options ?? new PopularitySignalOptions { Signals = [Signal()] }),
            _registry, _entities, updater, NullLogger<PopularitySignalReconciliationWorker>.Instance);
    }

    private void StubAggregate(long count) =>
        _search.AggregateAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns((EngagementAggResult?)new EngagementAggResult("count", AggregationKind.Count, MetricValue: count));

    // ── Two-page enumeration: UpdateAsync (proxied by SetPayloadAsync) runs once per parent
    //    across BOTH pages, and the cursor from page 1 feeds page 2's request. ─────────────
    [Fact]
    public async Task SweepSignalAsync_TwoPages_UpdatesEveryParentAcrossBothPages()
    {
        await _registry.RegisterAsync(ArticleSchema());
        await _registry.RegisterAsync(CommentSchema());
        StubAggregate(3);

        var page1 = new[] { new KeyedTenantRow("article-1", TenantA), new KeyedTenantRow("article-2", TenantA) };
        var page2 = new[] { new KeyedTenantRow("article-3", TenantA) };
        _entities.FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), null, 500).Returns(page1);
        _entities.FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), "article-2", 500).Returns(page2);
        _entities.FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), "article-3", 500).Returns([]);

        await BuildSut().SweepSignalAsync(Signal(), CancellationToken.None);

        foreach (var key in new[] { "article-1", "article-2", "article-3" })
            await _vector.Received(1).SetPayloadAsync(
                "articles_" + TenantA, IntelligenceStoreConsumer.KeyToUlong(key),
                Arg.Any<IReadOnlyDictionary<string, object>>());

        await _entities.Received(1).FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), null, 500);
        await _entities.Received(1).FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), "article-2", 500);
        await _entities.Received(1).FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), "article-3", 500);
    }

    // ── Unregistered schema: sweep skips without throwing and never pages. ──────────────
    [Fact]
    public async Task SweepSignalAsync_ParentTypeNotRegistered_SkipsWithoutThrowing()
    {
        // Neither Article nor Comment registered.
        var act = () => BuildSut().SweepSignalAsync(Signal(), CancellationToken.None);
        await act.Should().NotThrowAsync();

        await _entities.DidNotReceiveWithAnyArgs().FetchKeysAndTenantsPagedAsync(default!, default, default);
    }

    [Fact]
    public async Task SweepSignalAsync_RelationNotFoundOnParent_SkipsWithoutThrowing()
    {
        // Article registered but with no "Comments" relation configured.
        await _registry.RegisterAsync(ArticleSchema() with { Relations = [] });

        var act = () => BuildSut().SweepSignalAsync(Signal(), CancellationToken.None);
        await act.Should().NotThrowAsync();

        await _entities.DidNotReceiveWithAnyArgs().FetchKeysAndTenantsPagedAsync(default!, default, default);
    }

    // ── Null-tenant row: skipped without calling UpdateAsync (proxied by AggregateAsync). ──
    [Fact]
    public async Task SweepSignalAsync_RowWithNullTenantId_IsSkippedWithoutCallingUpdate()
    {
        await _registry.RegisterAsync(ArticleSchema());
        await _registry.RegisterAsync(CommentSchema());
        StubAggregate(1);

        var page = new[] { new KeyedTenantRow("article-1", null), new KeyedTenantRow("article-2", TenantA) };
        _entities.FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), null, 500).Returns(page);
        _entities.FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), "article-2", 500).Returns([]);

        await BuildSut().SweepSignalAsync(Signal(), CancellationToken.None);

        // Only the tenant-bearing row triggers an update.
        await _vector.Received(1).SetPayloadAsync(
            Arg.Any<string>(), Arg.Any<ulong>(), Arg.Any<IReadOnlyDictionary<string, object>>());
        await _vector.Received(1).SetPayloadAsync(
            "articles_" + TenantA, IntelligenceStoreConsumer.KeyToUlong("article-2"),
            Arg.Any<IReadOnlyDictionary<string, object>>());
    }

    // ── Per-row isolation: one parent's UpdateAsync throwing does not stop the sweep for the
    //    remaining parents (mirrors Task 4's per-item isolation). ──────────────────────────
    [Fact]
    public async Task SweepSignalAsync_OneParentUpdateThrows_DoesNotStopSweepForRemainingParents()
    {
        await _registry.RegisterAsync(ArticleSchema());
        await _registry.RegisterAsync(CommentSchema());
        StubAggregate(1);

        var page = new[] { new KeyedTenantRow("article-1", TenantA), new KeyedTenantRow("article-2", TenantA) };
        _entities.FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), null, 500).Returns(page);
        _entities.FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), "article-2", 500).Returns([]);

        // article-1's write to Qdrant throws a non-NotFound exception — the one exception
        // UpdateAsync does NOT swallow itself (contrast the two documented degrade cases in
        // PopularitySignalUpdater), so it propagates out to the worker's own per-row try/catch.
        // article-2's write succeeds.
        _vector.SetPayloadAsync(
                "articles_" + TenantA, IntelligenceStoreConsumer.KeyToUlong("article-1"),
                Arg.Any<IReadOnlyDictionary<string, object>>())
            .Returns(Task.FromException(new InvalidOperationException("qdrant unavailable")));

        var act = () => BuildSut().SweepSignalAsync(Signal(), CancellationToken.None);
        await act.Should().NotThrowAsync();

        await _vector.Received(1).SetPayloadAsync(
            "articles_" + TenantA, IntelligenceStoreConsumer.KeyToUlong("article-2"),
            Arg.Any<IReadOnlyDictionary<string, object>>());
    }
}
