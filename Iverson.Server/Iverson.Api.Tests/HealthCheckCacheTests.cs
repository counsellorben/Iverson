using FluentAssertions;
using Iverson.Api;
using Iverson.Events;
using Iverson.Sql;
using Iverson.StarRocks;
using Iverson.Vector;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iverson.Api.Tests;

// Regression coverage for the single-flight requirement on /health's fan-out: the endpoint is
// AllowAnonymous and publicly routed, and two of its four backend checks are writes (Qdrant
// EnsureCollectionAsync, Kafka ProduceAsync). A memoize-without-single-flight implementation
// (Tenancy/TenantStatusCache.cs's TryGetValue -> work -> Set shape) would let every concurrent
// caller on a cache miss run the fan-out independently. These tests assert HealthCheckCache
// does not do that: not just for two sequential calls inside the TTL window, but for N calls
// that are genuinely in flight at the same time on a cold cache.
public class HealthCheckCacheTests
{
    [Fact]
    public async Task GetAsync_TwoSequentialCallsInsideWindow_FansOutOnce()
    {
        var counters = new CallCounters();
        var cache = BuildCache(counters);

        await cache.GetAsync();
        await cache.GetAsync();

        counters.Postgres.Should().Be(1);
        counters.StarRocks.Should().Be(1);
        counters.Qdrant.Should().Be(1);
        counters.Kafka.Should().Be(1);
    }

    [Fact]
    public async Task GetAsync_NConcurrentCallsOnColdCache_FansOutExactlyOnce()
    {
        const int concurrentCallers = 25;
        var counters = new CallCounters();

        // Gate every backend fake on a shared TaskCompletionSource so the fan-out (once
        // started) stays in flight until every one of the N callers has already entered
        // GetAsync and is genuinely racing on the cold cache — not just N fast sequential
        // calls that happen to see the cache already populated. This is what makes the
        // assertion below fail against a TryGetValue -> work -> Set implementation with no
        // Lazy/lock: that shape would let all N callers observe a miss and each start (and
        // count) their own fan-out before any of them completes.
        var releaseGate = new TaskCompletionSource();
        var cache = BuildCache(counters, releaseGate.Task);

        var callerTasks = Enumerable.Range(0, concurrentCallers)
            .Select(_ => cache.GetAsync())
            .ToArray();

        // Give every caller a chance to actually start (and, on a non-single-flighting
        // implementation, to have already incremented the counters) before releasing the gate.
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        releaseGate.SetResult();

        await Task.WhenAll(callerTasks);

        counters.Postgres.Should().Be(1);
        counters.StarRocks.Should().Be(1);
        counters.Qdrant.Should().Be(1);
        counters.Kafka.Should().Be(1);
    }

    private static HealthCheckCache BuildCache(CallCounters counters, Task? gate = null)
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var options = Options.Create(new EngagementStoreOptions { Enabled = true });

        return new HealthCheckCache(
            new CountingRecordStoreQueryExecutor(counters, gate),
            new CountingEngagementStoreHealthCheck(counters, gate),
            new CountingVectorSchemaManager(counters, gate),
            new CountingEventProducer(counters, gate),
            options,
            memoryCache);
    }

    private sealed class CallCounters
    {
        private int _postgres;
        private int _starRocks;
        private int _qdrant;
        private int _kafka;

        public int Postgres => _postgres;
        public int StarRocks => _starRocks;
        public int Qdrant => _qdrant;
        public int Kafka => _kafka;

        public void IncrementPostgres() => Interlocked.Increment(ref _postgres);
        public void IncrementStarRocks() => Interlocked.Increment(ref _starRocks);
        public void IncrementQdrant() => Interlocked.Increment(ref _qdrant);
        public void IncrementKafka() => Interlocked.Increment(ref _kafka);
    }

    private sealed class CountingRecordStoreQueryExecutor(CallCounters counters, Task? gate) : IRecordStoreQueryExecutor
    {
        public Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null, bool tenantScoped = false, string? tenantId = null) =>
            throw new NotSupportedException("Not exercised by /health.");

        public Task<int> ExecuteAsync(string sql, object? param = null, bool tenantScoped = false, string? tenantId = null) =>
            throw new NotSupportedException("Not exercised by /health.");

        public async Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? param = null, bool tenantScoped = false, string? tenantId = null)
        {
            counters.IncrementPostgres();
            if (gate is not null)
                await gate;
            return (T)(object)1;
        }
    }

    private sealed class CountingEngagementStoreHealthCheck(CallCounters counters, Task? gate) : IEngagementStoreHealthCheck
    {
        public async Task<EngagementHealthStatus> CheckHealthAsync()
        {
            counters.IncrementStarRocks();
            if (gate is not null)
                await gate;
            return EngagementHealthStatus.Healthy;
        }

        public async Task<bool> IsHealthyAsync() => await CheckHealthAsync() == EngagementHealthStatus.Healthy;
    }

    private sealed class CountingVectorSchemaManager(CallCounters counters, Task? gate) : IVectorSchemaManager
    {
        public async Task EnsureCollectionAsync(string collectionName, ulong vectorSize)
        {
            counters.IncrementQdrant();
            if (gate is not null)
                await gate;
        }

        public Task ApplyCollectionAsync(CollectionSchema schema) =>
            throw new NotSupportedException("Not exercised by /health.");
    }

    private sealed class CountingEventProducer(CallCounters counters, Task? gate) : IEventProducer
    {
        public async Task ProduceAsync<T>(string topic, string key, T message) where T : class
        {
            counters.IncrementKafka();
            if (gate is not null)
                await gate;
        }

        public Task ProduceAsync(string topic, string key, string message) =>
            throw new NotSupportedException("Not exercised by /health.");
    }
}
