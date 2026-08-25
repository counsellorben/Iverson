using Iverson.Events;
using Iverson.Sql;
using Iverson.StarRocks;
using Iverson.Vector;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Iverson.Api;

public sealed record HealthChecks(
    bool Postgres,
    EngagementHealthStatus StarRocks,
    bool Qdrant,
    bool Kafka,
    bool EngagementEnabled);

// Single-flights the /health fan-out behind IMemoryCache. /health is AllowAnonymous and
// publicly routed, and two of its four backend checks are writes (a Qdrant
// EnsureCollectionAsync and a Kafka ProduceAsync). Deliberately departs from
// Tenancy/TenantStatusCache.cs's TryGetValue -> work -> Set shape: that pattern memoizes but
// does not single-flight, so every concurrent caller on a cache miss would run the fan-out
// independently, and N concurrent requests at expiry would each produce a Kafka message and a
// Qdrant collection-ensure.
//
// IMemoryCache.GetOrCreate is NOT itself an atomic "get-or-create": it is TryGetValue -> on
// miss, CreateEntry -> run the factory -> publish, and it returns the *locally created* value
// rather than re-reading whatever ended up published in the cache. So N callers racing the miss
// window each construct their own Lazy<Task<HealthChecks>> and each call .Value on a different
// instance, defeating the single-flight entirely (confirmed empirically: 32 threads released
// together against the GetOrCreate-based version produced 13 independent fan-outs, not 1). The
// fix is an explicit double-checked lock around the cache read/populate, with .Value called
// OUTSIDE the lock so the lock itself is held only for the cheap Lazy-construction/cache-Set,
// never for the fan-out's I/O.
public sealed class HealthCheckCache(
    IRecordStoreQueryExecutor db,
    IEngagementStoreHealthCheck sr,
    IVectorSchemaManager vector,
    IEventProducer kafka,
    IOptions<EngagementStoreOptions> engagementOptions,
    IMemoryCache cache)
{
    private const string CacheKey = "health-fan-out";

    // Must stay under the readiness probe's 10-second default period so a genuinely
    // recovered/degraded backend is reflected before the next probe.
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();

    public Task<HealthChecks> GetAsync()
    {
        if (cache.TryGetValue(CacheKey, out Lazy<Task<HealthChecks>>? lazy))
            return lazy!.Value;

        lock (_gate)
        {
            if (!cache.TryGetValue(CacheKey, out lazy))
            {
                lazy = new Lazy<Task<HealthChecks>>(FanOutAsync);
                cache.Set(CacheKey, lazy, Ttl);
            }
        }

        return lazy!.Value;
    }

    private async Task<HealthChecks> FanOutAsync()
    {
        var pgTask     = db.QuerySingleOrDefaultAsync<int>("SELECT 1").ContinueWith(t => t.IsCompletedSuccessfully && t.Result == 1);
        // Wrapped like the other three checks (not awaited directly): IEngagementStoreHealthCheck
        // is an interface, and nothing guarantees every implementation swallows its own
        // connection failures the way EngagementHealthChecker does. If CheckHealthAsync ever
        // faults, awaiting it unwrapped would fault Task.WhenAll below, and the Lazy this method
        // backs would then cache and rethrow that same faulted Task to every caller for the rest
        // of the 5s window — surfacing as a bare 500 instead of the 503-with-checks payload
        // ReadinessPolicy produces for a normal Unhealthy reading.
        var srTask     = sr.CheckHealthAsync().ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : EngagementHealthStatus.Unhealthy);
        var vectorTask = vector.EnsureCollectionAsync("iverson-probe", 4).ContinueWith(t => t.IsCompletedSuccessfully);
        var kafkaTask  = kafka.ProduceAsync("iverson.health.probe", "probe", new { ts = DateTime.UtcNow })
                             .ContinueWith(t => t.IsCompletedSuccessfully);

        await Task.WhenAll(pgTask, srTask, vectorTask, kafkaTask);

        return new HealthChecks(
            pgTask.Result,
            srTask.Result,
            vectorTask.Result,
            kafkaTask.Result,
            engagementOptions.Value.Enabled);
    }
}
