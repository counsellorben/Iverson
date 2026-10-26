using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Iverson.Sql.Tests;

public class TenantRepositoryTests
{
    private const string TableName = "IversonTenants";

    [Fact]
    public async Task InsertAsync_InsertsWithCreatedAt()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        var repo = new TenantRepository(TableName, sql);

        await repo.InsertAsync("tenant-1", "Display Name", "active");

        await sql.Received(1).ExecuteAsync(
            Arg.Is<string>(s => s.Contains($"INSERT INTO \"{TableName}\"") && s.Contains("@CreatedAt")),
            Arg.Any<object?>());
    }

    [Fact]
    public async Task SeedIfMissingAsync_InsertsWithOnConflictDoNothing()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        var repo = new TenantRepository(TableName, sql);

        await repo.SeedIfMissingAsync("tenant-1", "Display Name", "active");

        await sql.Received(1).ExecuteAsync(
            Arg.Is<string>(s => s.Contains($"INSERT INTO \"{TableName}\"") && s.Contains("ON CONFLICT") && s.Contains("DO NOTHING")),
            Arg.Any<object?>());
    }

    [Fact]
    public async Task GetAsync_SelectsById()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        sql.QuerySingleOrDefaultAsync<TenantRow>(Arg.Any<string>(), Arg.Any<object?>())
           .Returns(new TenantRow("tenant-1", "Display Name", "active", DateTime.UtcNow));
        var repo = new TenantRepository(TableName, sql);

        var result = await repo.GetAsync("tenant-1");

        result.Should().NotBeNull();
        await sql.Received(1).QuerySingleOrDefaultAsync<TenantRow>(
            Arg.Is<string>(s => s.Contains($"FROM \"{TableName}\"") && s.Contains("\"Id\" = @Id")),
            Arg.Any<object?>());
    }

    [Fact]
    public async Task ListAsync_OrdersByCreatedAt()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        sql.QueryAsync<TenantRow>(Arg.Any<string>(), Arg.Any<object?>())
           .Returns(new List<TenantRow> { new("tenant-1", "Display Name", "active", DateTime.UtcNow) });
        var repo = new TenantRepository(TableName, sql);

        var result = (await repo.ListAsync()).ToList();

        result.Should().ContainSingle();
        await sql.Received(1).QueryAsync<TenantRow>(
            Arg.Is<string>(s => s.Contains($"FROM \"{TableName}\"") && s.Contains("ORDER BY \"CreatedAt\"")),
            Arg.Any<object?>());
    }

    [Fact]
    public async Task UpdateStatusAsync_UpdatesStatusById()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        var repo = new TenantRepository(TableName, sql);

        await repo.UpdateStatusAsync("tenant-1", "suspended");

        await sql.Received(1).ExecuteAsync(
            Arg.Is<string>(s => s.Contains($"UPDATE \"{TableName}\"") && s.Contains("SET \"Status\" = @Status") && s.Contains("WHERE \"Id\" = @Id")),
            Arg.Any<object?>());
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public async Task UpdateStatusUnlessDeletedAsync_GuardsTheWriteInSql_AndReportsWhetherARowChanged(int affected, bool expected)
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(affected);
        var repo = new TenantRepository(TableName, sql);

        var changed = await repo.UpdateStatusUnlessDeletedAsync("tenant-1", "active");

        changed.Should().Be(expected);
        await sql.Received(1).ExecuteAsync(
            Arg.Is<string>(s => s.Contains($"UPDATE \"{TableName}\"") && s.Contains("SET \"Status\" = @Status")
                                && s.Contains("WHERE \"Id\" = @Id AND \"Status\" <> 'deleted'")),
            Arg.Any<object?>());
    }

    [Fact]
    public async Task DeleteAsync_DeletesById()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        var repo = new TenantRepository(TableName, sql);

        await repo.DeleteAsync("tenant-1");

        await sql.Received(1).ExecuteAsync(
            Arg.Is<string>(s => s.Contains($"DELETE FROM \"{TableName}\"") && s.Contains("WHERE \"Id\" = @Id")),
            Arg.Any<object?>());
    }

    // CSR round 12 §2.3: every write to the tenant registry notifies the api replicas' listeners in the
    // write's own statement, so the notification is sent exactly when the write commits.
    [Theory]
    [InlineData("Insert")]
    [InlineData("SeedIfMissing")]
    [InlineData("UpdateStatus")]
    [InlineData("UpdateStatusUnlessDeleted")]
    [InlineData("Delete")]
    public async Task EveryWrite_NotifiesTheAuthInvalidationChannel_InTheSameStatement(string write)
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        var repo = new TenantRepository(TableName, sql);

        await (write switch
        {
            "Insert"                    => repo.InsertAsync("tenant-1", "Display Name", "active"),
            "SeedIfMissing"             => repo.SeedIfMissingAsync("tenant-1", "Display Name", "active"),
            "UpdateStatus"              => repo.UpdateStatusAsync("tenant-1", "suspended"),
            "UpdateStatusUnlessDeleted" => (Task)repo.UpdateStatusUnlessDeletedAsync("tenant-1", "active"),
            "Delete"                    => repo.DeleteAsync("tenant-1"),
            _ => throw new ArgumentOutOfRangeException(nameof(write), write, null)
        });

        await sql.Received(1).ExecuteAsync(
            Arg.Is<string>(s => s.Contains("RETURNING pg_notify('iverson_auth_invalidation', 'tenant-status')")),
            Arg.Any<object?>());
    }
}
