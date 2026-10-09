using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Iverson.Sql.Tests;

/// <summary>
/// Every startup <c>EnsureTableAsync</c> must take its advisory lock in the same command, ahead of
/// the DDL, so two processes booting against a fresh database cannot race on the catalogue.
/// </summary>
public sealed class StartupDdlTests
{
    private const string Lock = "pg_advisory_xact_lock(hashtext('{0}')";

    private static async Task<string> CapturedSqlAsync(Func<IRecordStoreQueryExecutor, Task> ensure)
    {
        string? captured = null;
        var sql = Substitute.For<IRecordStoreQueryExecutor>();
        await sql.ExecuteAsync(
            Arg.Do<string>(s => captured = s), Arg.Any<object?>());
        await ensure(sql);
        return captured!;
    }

    private static void AssertLockPrecedesDdl(string captured, string lockName, string firstDdl)
    {
        var lockAt = captured.IndexOf(string.Format(Lock, lockName), StringComparison.Ordinal);
        var ddlAt  = captured.IndexOf(firstDdl, StringComparison.Ordinal);
        lockAt.Should().BeGreaterThanOrEqualTo(0);
        ddlAt.Should().BeGreaterThan(lockAt);
    }

    [Fact]
    public async Task SchemaRegistryRepository_EnsureTableAsync_TakesTheLockBeforeTheDdl() =>
        AssertLockPrecedesDdl(
            await CapturedSqlAsync(sql => new SchemaRegistryRepository(sql).EnsureTableAsync()),
            "_iverson_schema", "CREATE TABLE IF NOT EXISTS _iverson_schema");

    [Fact]
    public async Task EnrichmentStateRepository_EnsureTableAsync_TakesTheLockBeforeTheDdl() =>
        AssertLockPrecedesDdl(
            await CapturedSqlAsync(sql => new EnrichmentStateRepository(sql).EnsureTableAsync()),
            "iverson_enrichment_state", "CREATE TABLE IF NOT EXISTS iverson_enrichment_state");

    [Fact]
    public async Task TokenRevocationRepository_EnsureTableAsync_TakesTheLockBeforeTheDdl() =>
        AssertLockPrecedesDdl(
            await CapturedSqlAsync(sql => new TokenRevocationRepository(sql).EnsureTableAsync()),
            "iverson_token_revocations", "CREATE TABLE IF NOT EXISTS iverson_token_revocations");

    [Fact]
    public async Task DocumentRerenderQueueRepository_EnsureTableAsync_TakesTheLockBeforeEveryStatementOfTheDdl()
    {
        var captured = await CapturedSqlAsync(sql => new DocumentRerenderQueueRepository(sql).EnsureTableAsync());

        AssertLockPrecedesDdl(captured, DocumentRerenderQueueRepository.TableName, "CREATE TABLE IF NOT EXISTS");
        // One command, so the single lock covers the table and both indexes.
        captured.IndexOf("CREATE UNIQUE INDEX", StringComparison.Ordinal)
            .Should().BeGreaterThan(captured.IndexOf("pg_advisory_xact_lock", StringComparison.Ordinal));
    }
}
