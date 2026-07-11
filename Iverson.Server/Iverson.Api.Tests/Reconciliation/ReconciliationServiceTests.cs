using FluentAssertions;
using Iverson.Api.Reconciliation;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Events;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Reconciliation;

public class ReconciliationServiceTests
{
    private readonly IRecordStoreQueryExecutor _sql;
    private readonly IEntityRepository _entities;
    private readonly IReconciliationQueueRepository _queue;
    private readonly IEventProducer _events;
    private readonly SchemaRegistry _registry;
    private readonly ReconciliationService _sut;

    public ReconciliationServiceTests()
    {
        _sql = Substitute.For<IRecordStoreQueryExecutor>();
        _sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);
        _entities = Substitute.For<IEntityRepository>();
        _queue = Substitute.For<IReconciliationQueueRepository>();
        _events = Substitute.For<IEventProducer>();
        _registry = new SchemaRegistry(new SchemaRegistryRepository(_sql), NullLogger<SchemaRegistry>.Instance);
        _sut = new ReconciliationService(
            _registry, _sql, _entities, _queue, _events, NullLogger<ReconciliationService>.Instance);
    }

    [Fact]
    public async Task ReconcileTypeAsync_UnknownType_ReturnsNull()
    {
        var result = await _sut.ReconcileTypeAsync("NoSuchType");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ReconcileTypeAsync_RepublishesEveryRow_ReturnsCount()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _entities.FetchAllAsync(Arg.Is<TableSchema>(s => s.TableName == "authors"))
            .Returns(new[]
            {
                """{"Id":"11111111-1111-1111-1111-111111111111","Name":"Alice"}""",
                """{"Id":"22222222-2222-2222-2222-222222222222","Name":"Bob"}"""
            });

        var count = await _sut.ReconcileTypeAsync("Author");

        count.Should().Be(2);
        await _events.Received(2).ProduceAsync(
            EntityTopics.Updated,
            Arg.Any<string>(),
            Arg.Is<EntityEvent>(e => e.TypeName == "Author"));
    }

    [Fact]
    public async Task ProcessQueuedFailuresAsync_RepublishesRow_AndDeletesQueueEntry_OnSuccess()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        var queueId = Guid.NewGuid();
        _queue.PollQueuedFailuresAsync(Arg.Any<int>(), Arg.Any<int>())
            .Returns(new[] { new ReconciliationQueueRow(queueId, "Author", "author-1", 0) });
        _entities.FetchByKeyAsync(
                Arg.Is<TableSchema>(s => s.TableName == "authors"), Arg.Any<string>())
            .Returns("""{"Id":"author-1","Name":"Alice"}""");

        await _sut.ProcessQueuedFailuresAsync(CancellationToken.None);

        await _events.Received(1).ProduceAsync(
            EntityTopics.Updated,
            "author-1",
            Arg.Is<EntityEvent>(e => e.TypeName == "Author" && e.Key == "author-1"));

        await _queue.Received(1).DeleteRowAsync(queueId);
    }

    [Fact]
    public async Task ProcessQueuedFailuresAsync_RowNoLongerExistsInPostgres_DeletesQueueEntry_WithoutPublishing()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        var queueId = Guid.NewGuid();
        _queue.PollQueuedFailuresAsync(Arg.Any<int>(), Arg.Any<int>())
            .Returns(new[] { new ReconciliationQueueRow(queueId, "Author", "author-missing", 0) });
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
            .Returns((string?)null);

        await _sut.ProcessQueuedFailuresAsync(CancellationToken.None);

        await _events.DidNotReceiveWithAnyArgs().ProduceAsync(default!, default!, Arg.Any<EntityEvent>());
        await _queue.Received(1).DeleteRowAsync(queueId);
    }

    [Fact]
    public async Task ProcessQueuedFailuresAsync_ProduceAsyncThrows_IncrementsAttempts_AndKeepsQueueEntry()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        var queueId = Guid.NewGuid();
        _queue.PollQueuedFailuresAsync(Arg.Any<int>(), Arg.Any<int>())
            .Returns(new[] { new ReconciliationQueueRow(queueId, "Author", "author-1", 3) });
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
            .Returns("""{"Id":"author-1","Name":"Alice"}""");
        _events.ProduceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EntityEvent>())
               .Returns<Task>(_ => throw new InvalidOperationException("kafka still down"));

        await _sut.ProcessQueuedFailuresAsync(CancellationToken.None);

        await _queue.Received(1).RecordFailureAsync(queueId, 4, Arg.Any<string>());
    }

    [Fact]
    public async Task ProcessQueuedFailuresAsync_DeleteRow_PublishesDeletedEvent_UsingStoredPayload_WithoutQueryingEntityTable()
    {
        // AuthorSchema has no relations and no vector/chunk fields, so a correctly-resolved
        // TargetStores is Engagement only — distinct from the record default of All, which is
        // what a buggy replay (skipping schema resolution) would produce instead.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        var queueId = Guid.NewGuid();
        const string payload = """{"Id":"author-1","Name":"Alice"}""";
        _queue.PollQueuedFailuresAsync(Arg.Any<int>(), Arg.Any<int>())
            .Returns(new[] { new ReconciliationQueueRow(queueId, "Author", "author-1", 0, "Deleted", payload) });

        await _sut.ProcessQueuedFailuresAsync(CancellationToken.None);

        await _events.Received(1).ProduceAsync(
            EntityTopics.Deleted,
            "author-1",
            Arg.Is<EntityEvent>(e =>
                e.TypeName == "Author" && e.Key == "author-1" && e.PayloadJson == payload &&
                e.TargetStores == StoreTarget.Engagement));

        await _entities.DidNotReceiveWithAnyArgs().FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>());

        await _queue.Received(1).DeleteRowAsync(queueId);
    }

    [Fact]
    public async Task ProcessDeleteRowAsync_NoSchemaRegistered_FallsBackToTargetStoresAll()
    {
        // Schema deregistered since the delete happened is an edge case where the fix should
        // preserve current behavior (target every store) rather than throw or narrow scope.
        var queueId = Guid.NewGuid();
        const string payload = """{"Id":"ghost-1","Name":"Ghost"}""";
        _queue.PollQueuedFailuresAsync(Arg.Any<int>(), Arg.Any<int>())
            .Returns(new[] { new ReconciliationQueueRow(queueId, "NoSuchType", "ghost-1", 0, "Deleted", payload) });

        await _sut.ProcessQueuedFailuresAsync(CancellationToken.None);

        await _events.Received(1).ProduceAsync(
            EntityTopics.Deleted,
            "ghost-1",
            Arg.Is<EntityEvent>(e => e.TargetStores == StoreTarget.All));
    }

    [Fact]
    public async Task ProcessQueuedFailuresAsync_DeleteRow_ProduceAsyncThrows_IncrementsAttempts_AndKeepsQueueEntry()
    {
        var queueId = Guid.NewGuid();
        const string payload = """{"Id":"author-1","Name":"Alice"}""";
        _queue.PollQueuedFailuresAsync(Arg.Any<int>(), Arg.Any<int>())
            .Returns(new[] { new ReconciliationQueueRow(queueId, "Author", "author-1", 3, "Deleted", payload) });
        _events.ProduceAsync(EntityTopics.Deleted, Arg.Any<string>(), Arg.Any<EntityEvent>())
               .Returns<Task>(_ => throw new InvalidOperationException("kafka still down"));

        await _sut.ProcessQueuedFailuresAsync(CancellationToken.None);

        await _queue.Received(1).RecordFailureAsync(queueId, 4, Arg.Any<string>());
    }

    [Fact]
    public async Task ProcessQueuedFailuresAsync_QueriesExhaustedCount_WhenRowsHaveReachedMaxAttempts()
    {
        // No precedent in this codebase for substituting ILogger to assert log calls (all existing
        // tests use NullLogger for ReconciliationService), so prove the observability behavior by
        // asserting the exhausted-count query actually executes with the expected params,
        // rather than intercepting the logger.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _queue.PollQueuedFailuresAsync(Arg.Any<int>(), Arg.Any<int>())
            .Returns(Array.Empty<ReconciliationQueueRow>());
        _queue.CountExhaustedAsync(Arg.Any<int>()).Returns(3);

        await _sut.ProcessQueuedFailuresAsync(CancellationToken.None);

        await _queue.Received(1).CountExhaustedAsync(ReconciliationService.MaxAttempts);
    }

}
