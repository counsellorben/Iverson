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
// Qdrant collection-ensure. Caching a Lazy<Task<HealthChecks>> instead means concurrent
// callers on a miss all await the one in-flight fan-out.
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

    public Task<HealthChecks> GetAsync() =>
        cache.GetOrCreate(CacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Ttl;
            return new Lazy<Task<HealthChecks>>(FanOutAsync);
        })!.Value;

    private async Task<HealthChecks> FanOutAsync()
    {
        var pgTask     = db.QuerySingleOrDefaultAsync<int>("SELECT 1").ContinueWith(t => t.IsCompletedSuccessfully && t.Result == 1);
        var srTask     = sr.CheckHealthAsync();
        var vectorTask = vector.EnsureCollectionAsync("iverson-probe", 4).ContinueWith(t => t.IsCompletedSuccessfully);
        var kafkaTask  = kafka.ProduceAsync("iverson.health.probe", "probe", new { ts = DateTime.UtcNow })
                             .ContinueWith(t => t.IsCompletedSuccessfully);

        await Task.WhenAll(pgTask, srTask, vectorTask, kafkaTask);

        return new HealthChecks(
            pgTask.Result,
            await srTask,
            vectorTask.Result,
            kafkaTask.Result,
            engagementOptions.Value.Enabled);
    }
}
