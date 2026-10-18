using FluentAssertions;
using Iverson.Api.Tenancy;
using Npgsql;
using Xunit;

namespace Iverson.Api.Tests.Tenancy;

/// <summary>
/// CSR round 12 §7.3 and §6.2: the provisioned-tenant record against a real Postgres. The table and the api's
/// grant come from the worker's startup, run exactly as Program.cs runs it
/// (<see cref="ApiLoginGrantsPostgresContainerFixture"/>), and the record is read through the api's own login.
/// </summary>
[Trait("Category", "Integration")]
[Collection(ContainerCollection.Name)]
public sealed class ProvisionedTenantDirectoryPostgresIntegrationTests(ApiLoginGrantsPostgresContainerFixture fixture)
    : IClassFixture<ApiLoginGrantsPostgresContainerFixture>
{
    private static string UniqueTenantId() => "t" + Guid.NewGuid().ToString("N")[..16];

    [Fact]
    public async Task ATenantTheWorkerRecords_IsProvisioned_ForTheApiLogin()
    {
        var tenantId = UniqueTenantId();
        var api = new PostgresProvisionedTenantDirectory(fixture.Api);

        (await api.IsProvisionedAsync(tenantId)).Should().BeFalse();
        await new PostgresProvisionedTenantDirectory(fixture.Owner).RecordProvisionedAsync(tenantId);

        (await api.IsProvisionedAsync(tenantId)).Should().BeTrue();
    }

    [Fact]
    public async Task Recording_IsIdempotent()
    {
        var tenantId = UniqueTenantId();

        await new PostgresProvisionedTenantDirectory(fixture.Owner).RecordProvisionedAsync(tenantId);
        await new PostgresProvisionedTenantDirectory(fixture.Owner).RecordProvisionedAsync(tenantId);

        (await fixture.Owner.QuerySingleOrDefaultAsync<long>(
                """SELECT count(*) FROM "IversonStarRocksTenants" WHERE "TenantId" = @TenantId""",
                new { TenantId = tenantId }))
            .Should().Be(1);
    }

    [Fact]
    public async Task TheApiLogin_CanOnlyRead()
    {
        var tenantId = UniqueTenantId();
        await new PostgresProvisionedTenantDirectory(fixture.Owner).RecordProvisionedAsync(tenantId);

        Func<Task> record = () => new PostgresProvisionedTenantDirectory(fixture.Api).RecordProvisionedAsync(UniqueTenantId());
        Func<Task> update = () => fixture.Api.ExecuteAsync(
            """UPDATE "IversonStarRocksTenants" SET "ProvisionedAt" = now() WHERE "TenantId" = @TenantId""",
            new { TenantId = tenantId });
        Func<Task> delete = () => fixture.Api.ExecuteAsync(
            """DELETE FROM "IversonStarRocksTenants" WHERE "TenantId" = @TenantId""",
            new { TenantId = tenantId });

        foreach (var act in new[] { record, update, delete })
            (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
    }
}
