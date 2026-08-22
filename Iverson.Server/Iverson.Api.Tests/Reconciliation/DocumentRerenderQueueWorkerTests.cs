using FluentAssertions;
using Iverson.Api.Reconciliation;
using Iverson.Api.Schema;
using Iverson.Events;
using Iverson.Sql;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Reconciliation;

public class DocumentRerenderQueueWorkerTests
{
    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();
    private readonly IDocumentRerenderQueueRepository _queue = Substitute.For<IDocumentRerenderQueueRepository>();
    private readonly IEventProducer _events = Substitute.For<IEventProducer>();
    private readonly RecordingLogger<DocumentRerenderQueueWorker> _logger = new();
    private readonly SchemaRegistry _registry;
    private readonly IRecordStoreQueryExecutor _sql = Substitute.For<IRecordStoreQueryExecutor>();

    private const string TypeName = "Article";
    private const string TenantA  = "tenant-a";
    private static readonly string EntityKey = "11111111-0000-0000-0000-000000000001";

    private static readonly DocumentRerenderOptions DefaultOptions = new()
    {
        MaxAttempts = 5,
        BatchSize   = 100,
        PageSize    = 500,
        PollInterval = TimeSpan.FromSeconds(30)
    };

    public DocumentRerenderQueueWorkerTests()
    {
        _registry = new SchemaRegistry(new SchemaRegistryRepository(_sql), Microsoft.Extensions.Logging.Abstractions.NullLogger<SchemaRegistry>.Instance);
    }

    private static SchemaDescriptor ArticleSchema() => new()
    {
        TypeName      = TypeName,
        TableName     = "articles",
        KeyColumn     = new ColumnDescriptor("Id", "UUID", false),
        ScalarColumns = [],
        FkColumns     = [],
        VectorFields  = [],
        ChunkFields   = [],
        Relations     = [],
        TenantColumn  = "TenantId",
    };

    private DocumentRerenderQueueWorker BuildSut(DocumentRerenderOptions? options = null) =>
        new(_registry, _entities, _queue, _events, Microsoft.Extensions.Options.Options.Create(options ?? DefaultOptions), _logger);

    private void RegisterArticle() => _registry.RegisterAsync(ArticleSchema()).GetAwaiter().GetResult();

    private static DocumentRerenderQueueRow EntityRow(int attempts = 0, string? tenantId = TenantA) =>
        new(Guid.NewGuid(), tenantId, TypeName, EntityKey, null, attempts);

    private static DocumentRerenderQueueRow TypeRow(string? cursor = null, int attempts = 0) =>
        new(Guid.NewGuid(), null, TypeName, null, cursor, attempts);

    // ── Batch bounding ───────────────────────────────────────────────────────

    [Fact]
    public async Task TickAsync_PollsWithConfiguredMaxAttemptsAndBatchSize()
    {
        var opts = new DocumentRerenderOptions { MaxAttempts = 7, BatchSize = 42, PageSize = 500 };
        _queue.PollAsync(Arg.Any<int>(), Arg.Any<int>()).Returns([]);
        _queue.CountExhaustedAsync(Arg.Any<int>()).Returns(0);

        await BuildSut(opts).TickAsync(CancellationToken.None);

        await _queue.Received(1).PollAsync(7, 42);
    }

    // ── Vanished-row drop / re-fetch produces current state ────────────────

    [Fact]
    public async Task TickAsync_EntityRow_VanishedFromPostgres_IsDroppedNotResurrected()
    {
        RegisterArticle();
        var row = EntityRow();
        _queue.PollAsync(Arg.Any<int>(), Arg.Any<int>()).Returns([row]);
        _queue.CountExhaustedAsync(Arg.Any<int>()).Returns(0);
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), EntityKey).Returns((string?)null);

        await BuildSut().TickAsync(CancellationToken.None);

        await _entities.Received(1).FetchByKeyAsync(Arg.Any<TableSchema>(), EntityKey);
        await _events.DidNotReceiveWithAnyArgs().ProduceAsync(default!, default!, Arg.Any<EntityEvent>());
        await _queue.Received(1).DeleteRowAsync(row.Id);
    }

    [Fact]
    public async Task TickAsync_EntityRow_RepublishesCurrentPostgresState_NotEnqueueTimeState()
    {
        RegisterArticle();
        var row = EntityRow();
        _queue.PollAsync(Arg.Any<int>(), Arg.Any<int>()).Returns([row]);
        _queue.CountExhaustedAsync(Arg.Any<int>()).Returns(0);
        const string currentJson = """{"Id":"11111111-0000-0000-0000-000000000001","Title":"current"}""";
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), EntityKey).Returns(currentJson);

        EntityEvent? published = null;
        await _events.ProduceAsync(EntityTopics.Events, EntityKey, Arg.Do<EntityEvent>(e => published = e));

        await BuildSut().TickAsync(CancellationToken.None);

        published.Should().NotBeNull();
        published!.PayloadJson.Should().Be(currentJson);
        await _queue.Received(1).DeleteRowAsync(row.Id);
    }

    // ── Republished events carry SuppressRerenderCascade + Intelligence-only ─

    [Fact]
    public async Task TickAsync_EntityRow_PublishesWithSuppressCascadeAndIntelligenceOnlyTarget()
    {
        RegisterArticle();
        var row = EntityRow();
        _queue.PollAsync(Arg.Any<int>(), Arg.Any<int>()).Returns([row]);
        _queue.CountExhaustedAsync(Arg.Any<int>()).Returns(0);
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), EntityKey).Returns("""{"Id":"x"}""");

        EntityEvent? published = null;
        await _events.ProduceAsync(EntityTopics.Events, EntityKey, Arg.Do<EntityEvent>(e => published = e));

        await BuildSut().TickAsync(CancellationToken.None);

        published.Should().NotBeNull();
        published!.SuppressRerenderCascade.Should().BeTrue();
        published.TargetStores.Should().Be(StoreTarget.Intelligence);
    }

    // ── Failure recording ────────────────────────────────────────────────────

    [Fact]
    public async Task TickAsync_EntityRow_PublishThrows_RecordsFailure_AndDoesNotDelete()
    {
        RegisterArticle();
        var row = EntityRow(attempts: 2);
        _queue.PollAsync(Arg.Any<int>(), Arg.Any<int>()).Returns([row]);
        _queue.CountExhaustedAsync(Arg.Any<int>()).Returns(0);
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), EntityKey).Returns("""{"Id":"x"}""");
        _events.ProduceAsync(EntityTopics.Events, EntityKey, Arg.Any<EntityEvent>())
            .Returns<Task>(_ => throw new InvalidOperationException("kafka down"));

        await BuildSut().TickAsync(CancellationToken.None);

        await _queue.Received(1).RecordFailureAsync(row.Id, 3, "kafka down");
        await _queue.DidNotReceive().DeleteRowAsync(row.Id);
    }

    // ── Exhaustion warning (the row-not-returned-by-a-subsequent-poll half of this behavior is
    //    covered against real Postgres by DocumentRerenderQueuePostgresIntegrationTests
    //    .CountExhaustedAsync_CountsRowsAtOrAboveMaxAttempts_AndCountPendingCountsAllRows and the
    //    MaxAttempts WHERE clause in PollAsync itself; TickAsync_PollsWithConfiguredMaxAttemptsAndBatchSize
    //    above proves this worker feeds MaxAttempts through to PollAsync unmodified) ────────────

    [Fact]
    public async Task TickAsync_ExhaustedRowsPresent_LogsWarningNamingCount()
    {
        var opts = new DocumentRerenderOptions { MaxAttempts = 5, BatchSize = 100, PageSize = 500 };
        _queue.PollAsync(Arg.Any<int>(), Arg.Any<int>()).Returns([]);
        _queue.CountExhaustedAsync(5).Returns(3);

        await BuildSut(opts).TickAsync(CancellationToken.None);

        _logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("3") &&
            e.Message.Contains("5"));
    }

    [Fact]
    public async Task TickAsync_NoExhaustedRows_LogsNoWarning()
    {
        _queue.PollAsync(Arg.Any<int>(), Arg.Any<int>()).Returns([]);
        _queue.CountExhaustedAsync(Arg.Any<int>()).Returns(0);

        await BuildSut().TickAsync(CancellationToken.None);

        _logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task TickAsync_ExactlyOneExhaustedRow_StillLogsWarning()
    {
        // Boundary case distinct from the zero and three cases above — catches an off-by-one
        // on the "> 0" threshold (e.g. a mutation to "> 1").
        _queue.PollAsync(Arg.Any<int>(), Arg.Any<int>()).Returns([]);
        _queue.CountExhaustedAsync(5).Returns(1);

        await BuildSut().TickAsync(CancellationToken.None);

        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("1"));
    }

    // ── Type-level expansion: paged in key order, per-row tenant, delete on short page ─

    [Fact]
    public async Task TickAsync_TypeLevelRow_ExpandsPageInKeyOrder_WithEachRowsOwnTenant()
    {
        RegisterArticle();
        var typeRow = TypeRow(cursor: "cursor-0");
        _queue.PollAsync(Arg.Any<int>(), Arg.Any<int>()).Returns([typeRow]);
        _queue.CountExhaustedAsync(Arg.Any<int>()).Returns(0);

        var page = new[]
        {
            new KeyedTenantRow("key-1", "tenant-a"),
            new KeyedTenantRow("key-2", "tenant-b"),
        };
        _entities.FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), "cursor-0", Arg.Any<int>())
            .Returns(page);

        var opts = new DocumentRerenderOptions { MaxAttempts = 5, BatchSize = 100, PageSize = 500 };
        await BuildSut(opts).TickAsync(CancellationToken.None);

        Received.InOrder(() =>
        {
            _queue.EnqueueEntityAsync("tenant-a", TypeName, "key-1");
            _queue.EnqueueEntityAsync("tenant-b", TypeName, "key-2");
        });
        await _entities.Received(1).FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), "cursor-0", 500);
    }

    [Fact]
    public async Task TickAsync_TypeLevelRow_ShortPage_DeletesTypeRow_DoesNotAdvanceCursor()
    {
        RegisterArticle();
        var typeRow = TypeRow();
        _queue.PollAsync(Arg.Any<int>(), Arg.Any<int>()).Returns([typeRow]);
        _queue.CountExhaustedAsync(Arg.Any<int>()).Returns(0);

        var opts = new DocumentRerenderOptions { MaxAttempts = 5, BatchSize = 100, PageSize = 500 };
        // Short page: fewer rows than PageSize.
        _entities.FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns([new KeyedTenantRow("key-1", "tenant-a")]);

        await BuildSut(opts).TickAsync(CancellationToken.None);

        await _queue.Received(1).DeleteRowAsync(typeRow.Id);
        await _queue.DidNotReceiveWithAnyArgs().AdvanceCursorAsync(default, default!);
    }

    [Fact]
    public async Task TickAsync_TypeLevelRow_FullPage_AdvancesCursorToLastKey_DoesNotDelete()
    {
        RegisterArticle();
        var typeRow = TypeRow();
        _queue.PollAsync(Arg.Any<int>(), Arg.Any<int>()).Returns([typeRow]);
        _queue.CountExhaustedAsync(Arg.Any<int>()).Returns(0);

        var opts = new DocumentRerenderOptions { MaxAttempts = 5, BatchSize = 100, PageSize = 2 };
        // Full page: exactly PageSize rows.
        _entities.FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), Arg.Any<string?>(), 2)
            .Returns([new KeyedTenantRow("key-1", "tenant-a"), new KeyedTenantRow("key-2", "tenant-b")]);

        await BuildSut(opts).TickAsync(CancellationToken.None);

        await _queue.Received(1).AdvanceCursorAsync(typeRow.Id, "key-2");
        await _queue.DidNotReceive().DeleteRowAsync(typeRow.Id);
    }

    // ── Expansion failure: records against the type-level row, does not propagate,
    //    and leaves the tick's per-entity rows drained ──────────────────────────

    [Fact]
    public async Task TickAsync_ExpansionPagedReadThrows_RecordsFailureAgainstTypeRow_DoesNotPropagate()
    {
        RegisterArticle();
        var typeRow = TypeRow(attempts: 1);
        var entityRow = EntityRow();
        // Type-level row sorts first (EnqueuedAt ordering, as PollAsync would return it).
        _queue.PollAsync(Arg.Any<int>(), Arg.Any<int>()).Returns([typeRow, entityRow]);
        _queue.CountExhaustedAsync(Arg.Any<int>()).Returns(0);

        _entities.FetchKeysAndTenantsPagedAsync(Arg.Any<TableSchema>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns<IEnumerable<KeyedTenantRow>>(_ => throw new InvalidOperationException("db unreachable"));
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), EntityKey).Returns("""{"Id":"x"}""");

        // Must not throw.
        var act = () => BuildSut().TickAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();

        await _queue.Received(1).RecordFailureAsync(typeRow.Id, 2, "db unreachable");
        await _queue.DidNotReceive().DeleteRowAsync(typeRow.Id);

        // The tick's per-entity row is still drained despite the expansion failure.
        await _queue.Received(1).DeleteRowAsync(entityRow.Id);
    }

    // ── A test logger that records level + formatted message, so the exhaustion
    //    warning's content (not merely its presence) can be asserted. ──────────
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
