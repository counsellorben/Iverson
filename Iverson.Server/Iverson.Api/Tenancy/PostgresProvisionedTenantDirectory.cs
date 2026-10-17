using System.Collections.Concurrent;
using Iverson.Sql;
using Iverson.StarRocks;

namespace Iverson.Api.Tenancy;

/// <summary>
/// CSR round 12 §7.3: <see cref="IProvisionedTenantDirectory"/> over <see cref="StarRocksTenantSchema"/>'s table.
/// The api reads it through its SELECT grant; the worker, the table's owner, also records into it.
/// </summary>
internal sealed class PostgresProvisionedTenantDirectory(IRecordStoreQueryExecutor sql) : IProvisionedTenantDirectory
{
    // Positive answers only. Provisioning never undoes itself, so a tenant once found stays provisioned for the
    // life of the process; an absent tenant is asked again every time, because the worker may record it at any
    // moment.
    private readonly ConcurrentDictionary<string, byte> _provisioned = new(StringComparer.Ordinal);

    public async Task<bool> IsProvisionedAsync(string tenantId)
    {
        if (_provisioned.ContainsKey(tenantId)) return true;

        var found = await sql.QuerySingleOrDefaultAsync<bool>(
            $"""SELECT EXISTS (SELECT 1 FROM "{StarRocksTenantSchema.TableName}" WHERE "TenantId" = @TenantId)""",
            new { TenantId = tenantId });
        if (found) _provisioned.TryAdd(tenantId, 0);
        return found;
    }

    public async Task RecordProvisionedAsync(string tenantId)
    {
        await sql.ExecuteAsync(
            $"""
            INSERT INTO "{StarRocksTenantSchema.TableName}" ("TenantId", "ProvisionedAt")
            VALUES (@TenantId, now())
            ON CONFLICT ("TenantId") DO NOTHING
            """,
            new { TenantId = tenantId });
        _provisioned.TryAdd(tenantId, 0);
    }
}
