using System.Collections.Concurrent;

namespace Iverson.StarRocks.Tests;

/// <summary>
/// CSR round 12 §7.3: the provisioned-tenant record held in memory, standing in for the worker's Postgres
/// table so the StarRocks tests need no Postgres. <see cref="EngagementRepository.EnsureTenantProvisionedAsync"/>
/// records into it and the readers consult it, exactly as they do the Postgres one. Thread-safe, because the
/// integration tests provision and read from several tasks at once.
/// </summary>
internal sealed class InMemoryProvisionedTenantDirectory : IProvisionedTenantDirectory
{
    private readonly ConcurrentDictionary<string, byte> _recorded = new(StringComparer.Ordinal);

    public Task<bool> IsProvisionedAsync(string tenantId) => Task.FromResult(_recorded.ContainsKey(tenantId));

    public Task RecordProvisionedAsync(string tenantId)
    {
        _recorded.TryAdd(tenantId, 0);
        return Task.CompletedTask;
    }
}
