using Iverson.Sql;
using Microsoft.Extensions.Caching.Memory;

namespace Iverson.Api.Tenancy;

/// <summary>
/// Caches each tenant's status in this process for <see cref="Ttl"/>.
/// <para>
/// <c>TenantLifecycleGrpcService</c> calls <see cref="Invalidate"/> after every status write, so
/// the replica that served the write sees a suspension, reactivation or deletion immediately.
/// Every api replica, that one included, also learns of it through
/// <see cref="AuthInvalidationListener"/>, which calls <see cref="InvalidateAll"/> on the write's
/// notification, normally within seconds (CSR round 12 §2.3). <see cref="Ttl"/> stays the backstop
/// for a replica whose listener is reconnecting.
/// </para>
/// <para>
/// <see cref="InvalidateAll"/> moves every key to a new generation. A read that started before it
/// stores its result under the old generation's key, which no later read looks up, so a status read
/// before a change is never served after that change's invalidation; the orphaned entries expire with
/// <see cref="Ttl"/>. <see cref="Invalidate"/> drops one tenant's current entry, which a read in flight
/// can still repopulate, bounded by <see cref="Ttl"/>.
/// </para>
/// </summary>
public sealed class TenantStatusCache(
    ITenantRepository tenantRepository,
    IMemoryCache cache) : ITenantStatusCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private long _generation;

    // Prefixed so a raw tenantId can never collide with another consumer's key in the same
    // shared IMemoryCache: /health caches its composite result under "health-composite"
    // (Program.cs), and IMemoryCache.TryGetValue<TItem> returns true with a default value for a
    // cached null, so an un-namespaced tenant key could shadow -- or be shadowed by -- that entry.
    private static string KeyFor(long generation, string tenantId) => $"tenant-status:{generation}:{tenantId}";

    public async Task<string?> GetStatusAsync(string tenantId)
    {
        var key = KeyFor(Interlocked.Read(ref _generation), tenantId);
        if (cache.TryGetValue(key, out string? cachedStatus))
            return cachedStatus;

        var tenant = await tenantRepository.GetAsync(tenantId);
        var status = tenant?.Status;
        cache.Set(key, status, Ttl);
        return status;
    }

    public void Invalidate(string tenantId) => cache.Remove(KeyFor(Interlocked.Read(ref _generation), tenantId));

    public void InvalidateAll() => Interlocked.Increment(ref _generation);
}
