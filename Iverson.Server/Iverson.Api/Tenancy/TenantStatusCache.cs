using Iverson.Sql;
using Microsoft.Extensions.Caching.Memory;

namespace Iverson.Api.Tenancy;

public sealed class TenantStatusCache(
    ITenantRepository tenantRepository,
    IMemoryCache cache) : ITenantStatusCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    // Prefixed so a raw tenantId can never collide with another consumer's key in the same
    // shared IMemoryCache: /health caches its composite result under "health-composite"
    // (Program.cs), and IMemoryCache.TryGetValue<TItem> returns true with a default value for a
    // cached null, so an un-namespaced tenant key could shadow -- or be shadowed by -- that entry.
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
