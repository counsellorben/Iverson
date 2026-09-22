using Iverson.Sql;
using Microsoft.Extensions.Caching.Memory;

namespace Iverson.Api.Tenancy;

public sealed class TenantStatusCache(
    ITenantRepository tenantRepository,
    IMemoryCache cache) : ITenantStatusCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    // Prefixed so a raw tenantId can never collide with another consumer's key in the same
    // shared IMemoryCache (see HealthCheckCache.CacheKey's comment for the collision this closes
    // off: IMemoryCache.TryGetValue<TItem> returns true with a default value for a cached null,
    // so an un-namespaced key here could shadow -- or be shadowed by -- an unrelated cache entry).
    private static string KeyFor(string tenantId) => $"tenant-status:{tenantId}";

    public async Task<string?> GetStatusAsync(string tenantId)
    {
        if (cache.TryGetValue(KeyFor(tenantId), out string? cachedStatus))
            return cachedStatus;

        var tenant = await tenantRepository.GetAsync(tenantId);
        var status = tenant?.Status;
        cache.Set(KeyFor(tenantId), status, Ttl);
        return status;
    }
}
