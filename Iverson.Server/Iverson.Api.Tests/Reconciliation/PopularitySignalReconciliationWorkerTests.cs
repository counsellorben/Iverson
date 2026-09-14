using FluentAssertions;
using Iverson.Api.Consumers;
using Iverson.Api.Grpc;
using Iverson.Api.Reconciliation;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Sql;
using Iverson.StarRocks;
using Iverson.Vector;
using Microsoft.Extensions.Logging;
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

    private PopularitySignalReconciliationWorker BuildSut(
        PopularitySignalOptions? options = null,
        ILogger<PopularitySignalReconciliationWorker>? logger = null)
    {
        var updater = new PopularitySignalUpdater(
            _search, _vector, _tenantScope, NullLogger<PopularitySignalUpdater>.Instance);
        return new PopularitySignalReconciliationWorker(
            Options.Create(options ?? new PopularitySignalOptions { Signals = [Signal()] }),
            _registry, _entities, updater,
            logger ?? NullLogger<PopularitySignalReconciliationWorker>.Instance);
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
        _entities.FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), null, 500, Arg.Any<EntityAccess>()).Returns(page1);
        _entities.FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), "article-2", 500, Arg.Any<EntityAccess>()).Returns(page2);
        _entities.FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), "article-3", 500, Arg.Any<EntityAccess>()).Returns([]);

        await BuildSut().SweepSignalAsync(Signal(), CancellationToken.None);

        foreach (var key in new[] { "article-1", "article-2", "article-3" })
            await _vector.Received(1).SetPayloadAsync(
                _tenantScope.ResolveCollectionName("articles", TenantA, isChunks: false), IntelligenceStoreConsumer.KeyToUlong(key),
                Arg.Any<IReadOnlyDictionary<string, object>>());

        await _entities.Received(1).FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), null, 500, Arg.Any<EntityAccess>());
        await _entities.Received(1).FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), "article-2", 500, Arg.Any<EntityAccess>());
        await _entities.Received(1).FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), "article-3", 500, Arg.Any<EntityAccess>());
    }

    // ── Unregistered schema: sweep skips without throwing and never pages. ──────────────
    [Fact]
    public async Task SweepSignalAsync_ParentTypeNotRegistered_SkipsWithoutThrowing()
    {
        // Neither Article nor Comment registered.
        var act = () => BuildSut().SweepSignalAsync(Signal(), CancellationToken.None);
        await act.Should().NotThrowAsync();

        await _entities.DidNotReceiveWithAnyArgs().FetchKeysAndTenantsPagedAsync(default!, default, default, default!);
    }

    [Fact]
    public async Task SweepSignalAsync_RelationNotFoundOnParent_SkipsWithoutThrowing()
    {
        // Article registered but with no "Comments" relation configured.
        await _registry.RegisterAsync(ArticleSchema() with { Relations = [] });

        var act = () => BuildSut().SweepSignalAsync(Signal(), CancellationToken.None);
        await act.Should().NotThrowAsync();

        await _entities.DidNotReceiveWithAnyArgs().FetchKeysAndTenantsPagedAsync(default!, default, default, default!);
    }

    // ── Null-tenant row: skipped without calling UpdateAsync (proxied by AggregateAsync). ──
    [Fact]
    public async Task SweepSignalAsync_RowWithNullTenantId_IsSkippedWithoutCallingUpdate()
    {
        await _registry.RegisterAsync(ArticleSchema());
        await _registry.RegisterAsync(CommentSchema());
        StubAggregate(1);

        var page = new[] { new KeyedTenantRow("article-1", null), new KeyedTenantRow("article-2", TenantA) };
        _entities.FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), null, 500, Arg.Any<EntityAccess>()).Returns(page);
        _entities.FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), "article-2", 500, Arg.Any<EntityAccess>()).Returns([]);

        await BuildSut().SweepSignalAsync(Signal(), CancellationToken.None);

        // Only the tenant-bearing row triggers an update.
        await _vector.Received(1).SetPayloadAsync(
            Arg.Any<string>(), Arg.Any<ulong>(), Arg.Any<IReadOnlyDictionary<string, object>>());
        await _vector.Received(1).SetPayloadAsync(
            _tenantScope.ResolveCollectionName("articles", TenantA, isChunks: false), IntelligenceStoreConsumer.KeyToUlong("article-2"),
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
        _entities.FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), null, 500, Arg.Any<EntityAccess>()).Returns(page);
        _entities.FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), "article-2", 500, Arg.Any<EntityAccess>()).Returns([]);

        // article-1's write to Qdrant throws a non-NotFound exception — the one exception
        // UpdateAsync does NOT swallow itself (contrast the two documented degrade cases in
        // PopularitySignalUpdater), so it propagates out to the worker's own per-row try/catch.
        // article-2's write succeeds.
        _vector.SetPayloadAsync(
                _tenantScope.ResolveCollectionName("articles", TenantA, isChunks: false), IntelligenceStoreConsumer.KeyToUlong("article-1"),
                Arg.Any<IReadOnlyDictionary<string, object>>())
            .Returns(Task.FromException(new InvalidOperationException("qdrant unavailable")));

        var act = () => BuildSut().SweepSignalAsync(Signal(), CancellationToken.None);
        await act.Should().NotThrowAsync();

        await _vector.Received(1).SetPayloadAsync(
            _tenantScope.ResolveCollectionName("articles", TenantA, isChunks: false), IntelligenceStoreConsumer.KeyToUlong("article-2"),
            Arg.Any<IReadOnlyDictionary<string, object>>());
    }

    // Fails the first `failures` parents by key, succeeds for the rest — drives UpdateAsync's
    // aggregate catch, which is the Failed outcome the counter reacts to.
    private void StubAggregateFailingFor(params string[] failingKeys)
    {
        _search.AggregateAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns(ci =>
            {
                var q = ci.ArgAt<SearchQuery?>(1);
                var key = q?.Clauses.FirstOrDefault()?.Value?.StringVal;
                return key is not null && failingKeys.Contains(key)
                    ? Task.FromException<EngagementAggResult?>(new InvalidOperationException("starrocks down"))
                    : Task.FromResult<EngagementAggResult?>(
                        new EngagementAggResult("count", AggregationKind.Count, MetricValue: 1));
            });
    }

    private static KeyedTenantRow[] Page(int count, int from = 1) =>
        Enumerable.Range(from, count).Select(i => new KeyedTenantRow($"article-{i}", TenantA)).ToArray();

    // ── Spec test 2, threshold half: an all-Skipped sweep must run to COMPLETION. Skipped is a
    //    designed no-op (unprovisioned tenant), and it can affect every parent at once — if it
    //    counted toward the threshold the sweep would abandon itself during normal operation. ──
    [Fact]
    public async Task SweepSignalAsync_AllParentsSkipped_RunsToCompletionWithoutAbandoning()
    {
        await _registry.RegisterAsync(ArticleSchema());
        await _registry.RegisterAsync(CommentSchema());
        _search.AggregateAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns((EngagementAggResult?)null);

        var page = Page(8);
        _entities.FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), null, 500, Arg.Any<EntityAccess>()).Returns(page);
        _entities.FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), "article-8", 500, Arg.Any<EntityAccess>()).Returns([]);

        var logs = new RecordingLogger<PopularitySignalReconciliationWorker>();
        await BuildSut(logger: logs).SweepSignalAsync(Signal(), CancellationToken.None);

        // Paged to exhaustion rather than abandoning at 5.
        await _entities.Received(1).FetchKeysAndTenantsPagedAsync(
            Arg.Any<TableSchema>(), "article-8", 500, Arg.Any<EntityAccess>());
        logs.Entries.Should().NotContain(e => e.Message.Contains("Abandoning sweep"));
    }

    // ── Spec test 3: five CONSECUTIVE failures abandon the sweep with EXACTLY ONE summary line. ──
    [Fact]
    public async Task SweepSignalAsync_FiveConsecutiveFailures_AbandonsWithExactlyOneSummary()
    {
        await _registry.RegisterAsync(ArticleSchema());
        await _registry.RegisterAsync(CommentSchema());
        StubAggregateFailingFor("article-1", "article-2", "article-3", "article-4", "article-5");

        _entities.FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), null, 500, Arg.Any<EntityAccess>())
            .Returns(Page(8));

        var logs = new RecordingLogger<PopularitySignalReconciliationWorker>();
        await BuildSut(logger: logs).SweepSignalAsync(Signal(), CancellationToken.None);

        var summary = logs.Entries.Should().ContainSingle(e => e.Message.Contains("Abandoning sweep")).Which;
        summary.Message.Should().Contain("after 5 consecutive parent-update failures");
        summary.Level.Should().Be(LogLevel.Error);

        // Abandoned mid-page: parents 6-8 were never attempted, and no second page was requested.
        await _vector.DidNotReceive().SetPayloadAsync(
            Arg.Any<string>(), IntelligenceStoreConsumer.KeyToUlong("article-6"),
            Arg.Any<IReadOnlyDictionary<string, object>>());
        await _entities.DidNotReceive().FetchKeysAndTenantsPagedAsync(
            Arg.Any<TableSchema>(), "article-8", 500, Arg.Any<EntityAccess>());
    }

    // ── Spec test 4: scattered, non-consecutive failures must NOT abandon. A systemic fault fails
    //    every parent; a poisoned row fails one. Cumulative counting would abandon a 400k-parent
    //    sweep over five unrelated failures — this is the test that pins the difference. ──
    [Fact]
    public async Task SweepSignalAsync_ScatteredNonConsecutiveFailures_DoesNotAbandon()
    {
        await _registry.RegisterAsync(ArticleSchema());
        await _registry.RegisterAsync(CommentSchema());
        // Six failures, never more than two in a row.
        StubAggregateFailingFor("article-1", "article-2", "article-4", "article-5", "article-7", "article-8");

        var page = Page(9);
        _entities.FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), null, 500, Arg.Any<EntityAccess>()).Returns(page);
        _entities.FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), "article-9", 500, Arg.Any<EntityAccess>()).Returns([]);

        var logs = new RecordingLogger<PopularitySignalReconciliationWorker>();
        await BuildSut(logger: logs).SweepSignalAsync(Signal(), CancellationToken.None);

        logs.Entries.Should().NotContain(e => e.Message.Contains("Abandoning sweep"));
        await _entities.Received(1).FetchKeysAndTenantsPagedAsync(
            Arg.Any<TableSchema>(), "article-9", 500, Arg.Any<EntityAccess>());
    }

    // ── Fix round 1: five CONSECUTIVE PROPAGATING exceptions — as opposed to the three tests
    //    above, which all drive Failed via StubAggregateFailingFor throwing INSIDE
    //    AggregateAsync, caught by UpdateAsync's own try/catch and returned as a normal Failed
    //    value. That path never touches the seeded `outcome = Failed` local. This test instead
    //    makes SetPayloadAsync throw a non-NotFound exception, which UpdateAsync does NOT catch
    //    (contrast the documented NotFound degrade case) — the exception propagates out to the
    //    worker's own per-row try/catch, which never assigns `outcome`, so only the seed reaching
    //    the counter-increment branch makes this abandon. Mirrors the pre-existing
    //    SweepSignalAsync_OneParentUpdateThrows_DoesNotStopSweepForRemainingParents idiom. ──
    [Fact]
    public async Task SweepSignalAsync_FiveConsecutivePropagatingExceptions_AbandonsSweep()
    {
        await _registry.RegisterAsync(ArticleSchema());
        await _registry.RegisterAsync(CommentSchema());
        StubAggregate(1); // must reach the Qdrant write — an unstubbed/null aggregate returns
                           // Skipped at the null-result branch and never gets there.

        foreach (var key in new[] { "article-1", "article-2", "article-3", "article-4", "article-5" })
            _vector.SetPayloadAsync(
                    _tenantScope.ResolveCollectionName("articles", TenantA, isChunks: false), IntelligenceStoreConsumer.KeyToUlong(key),
                    Arg.Any<IReadOnlyDictionary<string, object>>())
                .Returns(Task.FromException(new InvalidOperationException("qdrant unavailable")));

        _entities.FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), null, 500, Arg.Any<EntityAccess>())
            .Returns(Page(8));

        var logs = new RecordingLogger<PopularitySignalReconciliationWorker>();
        await BuildSut(logger: logs).SweepSignalAsync(Signal(), CancellationToken.None);

        var summary = logs.Entries.Should().ContainSingle(e => e.Message.Contains("Abandoning sweep")).Which;
        summary.Message.Should().Contain("after 5 consecutive parent-update failures");
        summary.Level.Should().Be(LogLevel.Error);
    }

    // ── A test logger that records level + formatted message, so the sweep-abandonment
    //    error's content (not merely its presence) can be asserted. ──────────
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
