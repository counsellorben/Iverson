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
    private readonly IPostgresRepository _sql;
    private readonly IEventProducer _events;
    private readonly SchemaRegistry _registry;
    private readonly ReconciliationService _sut;

    public ReconciliationServiceTests()
    {
        _sql = Substitute.For<IPostgresRepository>();
        _sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);
        _events = Substitute.For<IEventProducer>();
        _registry = new SchemaRegistry(_sql, NullLogger<SchemaRegistry>.Instance);
        _sut = new ReconciliationService(_registry, _sql, _events, NullLogger<ReconciliationService>.Instance);
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
        _sql.QueryAsync<string>(Arg.Is<string>(s => s.Contains("\"authors\"")), Arg.Any<object?>())
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
        var queueId = Guid.NewGuid().ToString();
        _sql.QueryAsync<ReconciliationQueueRow>(
                Arg.Is<string>(s => s.Contains(ReconciliationSchema.TableName)), Arg.Any<object?>())
            .Returns(new[] { new ReconciliationQueueRow(queueId, "Author", "author-1", 0) });
        _sql.QuerySingleOrDefaultAsync<string>(
                Arg.Is<string>(s => s.Contains("\"authors\"")), Arg.Any<object?>())
            .Returns("""{"Id":"author-1","Name":"Alice"}""");

        await _sut.ProcessQueuedFailuresAsync(CancellationToken.None);

        await _events.Received(1).ProduceAsync(
            EntityTopics.Updated,
            "author-1",
            Arg.Is<EntityEvent>(e => e.TypeName == "Author" && e.Key == "author-1"));

        await _sql.Received(1).ExecuteAsync(
            Arg.Is<string>(s => s.Contains("DELETE") && s.Contains(ReconciliationSchema.TableName)),
            Arg.Any<object>());
    }

    [Fact]
    public async Task ProcessQueuedFailuresAsync_RowNoLongerExistsInPostgres_DeletesQueueEntry_WithoutPublishing()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        var queueId = Guid.NewGuid().ToString();
        _sql.QueryAsync<ReconciliationQueueRow>(
                Arg.Is<string>(s => s.Contains(ReconciliationSchema.TableName)), Arg.Any<object?>())
            .Returns(new[] { new ReconciliationQueueRow(queueId, "Author", "author-missing", 0) });
        _sql.QuerySingleOrDefaultAsync<string>(Arg.Any<string>(), Arg.Any<object?>())
            .Returns((string?)null);

        await _sut.ProcessQueuedFailuresAsync(CancellationToken.None);

        await _events.DidNotReceiveWithAnyArgs().ProduceAsync(default!, default!, Arg.Any<EntityEvent>());
        await _sql.Received(1).ExecuteAsync(
            Arg.Is<string>(s => s.Contains("DELETE") && s.Contains(ReconciliationSchema.TableName)),
            Arg.Any<object>());
    }

    [Fact]
    public async Task ProcessQueuedFailuresAsync_ProduceAsyncThrows_IncrementsAttempts_AndKeepsQueueEntry()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        var queueId = Guid.NewGuid().ToString();
        _sql.QueryAsync<ReconciliationQueueRow>(
                Arg.Is<string>(s => s.Contains(ReconciliationSchema.TableName)), Arg.Any<object?>())
            .Returns(new[] { new ReconciliationQueueRow(queueId, "Author", "author-1", 3) });
        _sql.QuerySingleOrDefaultAsync<string>(Arg.Any<string>(), Arg.Any<object?>())
            .Returns("""{"Id":"author-1","Name":"Alice"}""");
        _events.ProduceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EntityEvent>())
               .Returns<Task>(_ => throw new InvalidOperationException("kafka still down"));

        object? capturedUpdateParams = null;
        _sql.WhenForAnyArgs(s => s.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()))
            .Do(call =>
            {
                var executedSql = call.ArgAt<string>(0);
                if (executedSql.Contains("UPDATE") && executedSql.Contains(ReconciliationSchema.TableName))
                    capturedUpdateParams = call.ArgAt<object?>(1);
            });

        await _sut.ProcessQueuedFailuresAsync(CancellationToken.None);

        capturedUpdateParams.Should().NotBeNull();
        var dynamicParams = (dynamic)capturedUpdateParams!;
        ((int)dynamicParams.Attempts).Should().Be(4);
    }
}
