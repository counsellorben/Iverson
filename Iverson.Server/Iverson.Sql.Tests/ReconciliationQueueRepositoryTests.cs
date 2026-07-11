using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Iverson.Sql.Tests;

public class ReconciliationQueueRepositoryTests
{
    private const string TableName = "IversonReconciliationQueue";

    [Fact]
    public async Task PollQueuedFailuresAsync_FiltersByMaxAttemptsAndOrdersByEnqueuedAt()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        sql.QueryAsync<ReconciliationQueueRow>(Arg.Any<string>(), Arg.Any<object?>())
           .Returns(new List<ReconciliationQueueRow> { new(Guid.NewGuid(), "Article", "k1", 0) });
        var repo = new ReconciliationQueueRepository(TableName, sql);

        var result = (await repo.PollQueuedFailuresAsync(10, 100)).ToList();

        result.Should().ContainSingle();
        await sql.Received(1).QueryAsync<ReconciliationQueueRow>(
            Arg.Is<string>(s => s.Contains("ORDER BY \"EnqueuedAt\"") && s.Contains("WHERE \"Attempts\" < @MaxAttempts")),
            Arg.Any<object?>());
    }

    [Fact]
    public async Task CountExhaustedAsync_CountsAttemptsAtOrAboveMax()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        sql.QuerySingleOrDefaultAsync<int>(Arg.Any<string>(), Arg.Any<object?>()).Returns(3);
        var repo = new ReconciliationQueueRepository(TableName, sql);

        var count = await repo.CountExhaustedAsync(10);

        count.Should().Be(3);
        await sql.Received(1).QuerySingleOrDefaultAsync<int>(
            Arg.Is<string>(s => s.Contains("\"Attempts\" >= @MaxAttempts")), Arg.Any<object?>());
    }

    [Fact]
    public async Task RecordFailureAsync_UpdatesAttemptsErrorAndTimestamp()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        var repo = new ReconciliationQueueRepository(TableName, sql);
        var id = Guid.NewGuid();

        await repo.RecordFailureAsync(id, 1, "boom");

        await sql.Received(1).ExecuteAsync(
            Arg.Is<string>(s => s.Contains("UPDATE \"IversonReconciliationQueue\"") && s.Contains("SET \"Attempts\"")),
            Arg.Any<object?>());
    }

    [Fact]
    public async Task DeleteRowAsync_DeletesById()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        var repo = new ReconciliationQueueRepository(TableName, sql);

        await repo.DeleteRowAsync(Guid.NewGuid());

        await sql.Received(1).ExecuteAsync(
            Arg.Is<string>(s => s.Contains("DELETE FROM \"IversonReconciliationQueue\"")), Arg.Any<object?>());
    }

    [Fact]
    public async Task CountPendingAsync_CountsEveryRow()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        sql.QuerySingleOrDefaultAsync<int>(Arg.Any<string>(), Arg.Any<object?>()).Returns(7);
        var repo = new ReconciliationQueueRepository(TableName, sql);

        var count = await repo.CountPendingAsync();

        count.Should().Be(7);
        await sql.Received(1).QuerySingleOrDefaultAsync<int>(
            Arg.Is<string>(s => s.Contains($"""SELECT COUNT(*) FROM "{TableName}" """)),
            Arg.Any<object?>());
    }
}
