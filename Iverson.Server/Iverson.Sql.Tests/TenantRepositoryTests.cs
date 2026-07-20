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
           .Returns(new TenantRow("tenant-1", "Display Name", "active", DateTimeOffset.UtcNow));
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
           .Returns(new List<TenantRow> { new("tenant-1", "Display Name", "active", DateTimeOffset.UtcNow) });
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
}
