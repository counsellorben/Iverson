using FluentAssertions;
using Iverson.Api.Tenancy;
using Iverson.Sql;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Tenancy;

/// <summary>
/// CSR round 12 §7.3: the Postgres-backed provisioned-tenant record caches a tenant once it is found, and only
/// then. Provisioning never undoes itself, but a tenant not yet recorded may be recorded by the worker at any
/// moment, so a negative answer is never kept.
/// </summary>
public class PostgresProvisionedTenantDirectoryTests
{
    private readonly IRecordStoreQueryExecutor _sql = Substitute.For<IRecordStoreQueryExecutor>();

    private void PostgresAnswers(bool first, params bool[] rest) =>
        _sql.QuerySingleOrDefaultAsync<bool>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<RecordStoreRole>(), Arg.Any<string?>())
            .Returns(first, rest);

    [Fact]
    public async Task ARecordedTenant_IsCached_SoPostgresIsAskedOnce()
    {
        PostgresAnswers(true);
        var directory = new PostgresProvisionedTenantDirectory(_sql);

        (await directory.IsProvisionedAsync("acme")).Should().BeTrue();
        (await directory.IsProvisionedAsync("acme")).Should().BeTrue();

        await _sql.Received(1).QuerySingleOrDefaultAsync<bool>(
            Arg.Is<string>(s => s.Contains("\"IversonStarRocksTenants\"")), Arg.Any<object?>(),
            Arg.Any<RecordStoreRole>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task AnUnrecordedTenant_IsAskedAgain_BecauseTheWorkerMayRecordItAtAnyMoment()
    {
        PostgresAnswers(false, true);
        var directory = new PostgresProvisionedTenantDirectory(_sql);

        (await directory.IsProvisionedAsync("acme")).Should().BeFalse();
        (await directory.IsProvisionedAsync("acme")).Should().BeTrue();

        await _sql.Received(2).QuerySingleOrDefaultAsync<bool>(
            Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<RecordStoreRole>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Recording_InsertsIdempotently_AndCachesTheTenant()
    {
        var directory = new PostgresProvisionedTenantDirectory(_sql);

        await directory.RecordProvisionedAsync("acme");

        await _sql.Received(1).ExecuteAsync(
            Arg.Is<string>(s => s.Contains("INSERT INTO \"IversonStarRocksTenants\"")
                             && s.Contains("ON CONFLICT (\"TenantId\") DO NOTHING")),
            Arg.Any<object?>(), Arg.Any<RecordStoreRole>(), Arg.Any<string?>());
        (await directory.IsProvisionedAsync("acme")).Should().BeTrue();
        await _sql.DidNotReceive().QuerySingleOrDefaultAsync<bool>(
            Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<RecordStoreRole>(), Arg.Any<string?>());
    }
}
