using Iverson.Sql;
using Microsoft.Extensions.Caching.Memory;

namespace Iverson.Api.Tenancy;

public sealed class TenantStatusCache(
    ITenantRepository tenantRepository,
    IMemoryCache cache) : ITenantStatusCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    public async Task<string?> GetStatusAsync(string tenantId)
    {
        if (cache.TryGetValue(tenantId, out string? cachedStatus))
            return cachedStatus;

        var tenant = await tenantRepository.GetAsync(tenantId);
        var status = tenant?.Status;
        cache.Set(tenantId, status, Ttl);
        return status;
    }
}
