using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Iverson.Sql.Tests;

public class DlqRepositoryTests
{
    private const string TableName = "IversonDlqMessages";

    [Fact]
    public async Task InsertAsync_InsertsAsUnreplayed()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        var repo = new DlqRepository(TableName, sql);
        var msg = new DlqMessage("topic", "group", "key", "value", "Ex", "msg", 1, DateTimeOffset.UtcNow);

        await repo.InsertAsync(msg);

        await sql.Received(1).ExecuteAsync(
            Arg.Is<string>(s => s.Contains($"INSERT INTO \"{TableName}\"") && s.Contains("false")),
            Arg.Any<object?>());
    }

    [Fact]
    public async Task ListUnreplayedAsync_FiltersByReplayedFalse_OrdersByFailedAtDesc()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        sql.QueryAsync<DlqRow>(Arg.Any<string>(), Arg.Any<object?>())
           .Returns(new List<DlqRow> { new(Guid.NewGuid(), "t", "g", "k", null, null, 0, DateTimeOffset.UtcNow, false) });
        var repo = new DlqRepository(TableName, sql);

        var result = (await repo.ListUnreplayedAsync(200)).ToList();

        result.Should().ContainSingle();
        await sql.Received(1).QueryAsync<DlqRow>(
            Arg.Is<string>(s => s.Contains("\"Replayed\" = false") && s.Contains("ORDER BY \"FailedAt\" DESC")),
            Arg.Any<object?>());
    }

    [Fact]
    public async Task GetUnreplayedByIdAsync_FiltersByIdAndReplayedFalse()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        sql.QuerySingleOrDefaultAsync<DlqReplayRow>(Arg.Any<string>(), Arg.Any<object?>())
           .Returns(new DlqReplayRow("t", "k", "v"));
        var repo = new DlqRepository(TableName, sql);

        var result = await repo.GetUnreplayedByIdAsync(Guid.NewGuid());

        result.Should().NotBeNull();
        await sql.Received(1).QuerySingleOrDefaultAsync<DlqReplayRow>(
            Arg.Is<string>(s => s.Contains("\"Id\" = @Id AND \"Replayed\" = false")), Arg.Any<object?>());
    }

    [Fact]
    public async Task MarkReplayedAsync_SetsReplayedTrueById()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        var repo = new DlqRepository(TableName, sql);

        await repo.MarkReplayedAsync(Guid.NewGuid());

        await sql.Received(1).ExecuteAsync(
            Arg.Is<string>(s => s.Contains("SET \"Replayed\" = true WHERE \"Id\" = @Id")), Arg.Any<object?>());
    }

    [Fact]
    public async Task CountUnreplayedAsync_CountsRowsWhereReplayedFalse()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        sql.QuerySingleOrDefaultAsync<int>(Arg.Any<string>(), Arg.Any<object?>()).Returns(4);
        var repo = new DlqRepository(TableName, sql);

        var count = await repo.CountUnreplayedAsync();

        count.Should().Be(4);
        await sql.Received(1).QuerySingleOrDefaultAsync<int>(
            Arg.Is<string>(s => s.Contains("\"Replayed\" = false")), Arg.Any<object?>());
    }
}
