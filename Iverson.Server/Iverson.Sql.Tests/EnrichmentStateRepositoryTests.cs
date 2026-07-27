using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Iverson.Sql.Tests;

public class EnrichmentStateRepositoryTests
{
    [Fact]
    public async Task EnsureTableAsync_CreatesTableIfNotExists()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        var repo = new EnrichmentStateRepository(sql);

        await repo.EnsureTableAsync();

        await sql.Received(1).ExecuteAsync(
            Arg.Is<string>(s => s.Contains("CREATE TABLE IF NOT EXISTS iverson_enrichment_state")), Arg.Any<object?>());
    }

    [Fact]
    public async Task GetHashAsync_QueriesByTenantTypeAndKey()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        sql.QuerySingleOrDefaultAsync<string>(Arg.Any<string>(), Arg.Any<object?>()).Returns("hash1");
        var repo = new EnrichmentStateRepository(sql);

        var result = await repo.GetHashAsync("tenant-a", "Article", "k1");

        result.Should().Be("hash1");
        await sql.Received(1).QuerySingleOrDefaultAsync<string>(
            Arg.Is<string>(s => s.Contains("SELECT source_hash FROM iverson_enrichment_state")),
            Arg.Is<object>(p =>
                (string)p.GetType().GetProperty("TenantId")!.GetValue(p)! == "tenant-a" &&
                (string)p.GetType().GetProperty("TypeName")!.GetValue(p)! == "Article" &&
                (string)p.GetType().GetProperty("EntityKey")!.GetValue(p)! == "k1"));
    }

    [Fact]
    public async Task UpsertAsync_ExecutesOnTheGivenTransactionContext_WithOnConflictUpdate()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        var tx = Substitute.For<IDbTransactionContext>();
        var repo = new EnrichmentStateRepository(sql);
        var now = DateTimeOffset.UtcNow;

        await repo.UpsertAsync(tx, "tenant-a", "Article", "k1", "hash1", now);

        await tx.Received(1).ExecuteAsync(
            Arg.Is<string>(s => s.Contains("INSERT INTO iverson_enrichment_state") && s.Contains("ON CONFLICT (tenant_id, type_name, entity_key) DO UPDATE")),
            Arg.Any<object?>());
        await sql.DidNotReceive().ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>());
    }

    [Fact]
    public async Task DeleteAsync_DeletesByTenantTypeAndKey()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        var repo = new EnrichmentStateRepository(sql);

        await repo.DeleteAsync("tenant-a", "Article", "k1");

        await sql.Received(1).ExecuteAsync(
            Arg.Is<string>(s => s.Contains("DELETE FROM iverson_enrichment_state")),
            Arg.Is<object>(p =>
                (string)p.GetType().GetProperty("TenantId")!.GetValue(p)! == "tenant-a" &&
                (string)p.GetType().GetProperty("TypeName")!.GetValue(p)! == "Article" &&
                (string)p.GetType().GetProperty("EntityKey")!.GetValue(p)! == "k1"));
    }
}
