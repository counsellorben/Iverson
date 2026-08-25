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
// that are genuinely in flight at the same time on a cold cache — and also that a faulted
// backend check doesn't get cached and rethrown to every caller for the rest of the window.
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

    // Regression test for the review finding that IMemoryCache.GetOrCreate is NOT an atomic
    // get-or-create: it is TryGetValue -> on miss, CreateEntry -> run factory -> publish, and it
    // returns the *locally created* value rather than re-reading whatever ended up published in
    // the cache. A prior version of this test used Enumerable.Range(...).Select(_ =>
    // cache.GetAsync()) on a single thread, which starts every call synchronously one after
    // another — caller 0 publishes the cache entry before GetAsync() even returns control to the
    // loop, so callers 1..N-1 are cache HITS on the same Lazy and no race is ever created. That
    // version passed against both the correct implementation and the broken GetOrCreate-based
    // one, so it proved nothing about single-flighting.
    //
    // This version uses real OS/thread-pool threads (Task.Run) synchronized on a Barrier so all
    // callers enter HealthCheckCache.GetAsync at (as close to) the same instant as the runtime
    // allows — the actual precondition for the GetOrCreate race to manifest. The shared
    // TaskCompletionSource gate additionally keeps any fan-out that DOES start from completing
    // (and publishing its result) until every caller has had a chance to reach and pass the
    // race window, which is what makes the failure reproducible rather than a rare flake:
    // without it, whichever caller wins the race can complete and populate the cache before the
    // rest even reach GetAsync, so a broken implementation could still get lucky and only fan
    // out once.
    //
    // Verified against the pre-fix HealthCheckCache (cache.GetOrCreate(key, entry => new
    // Lazy<Task<HealthChecks>>(FanOutAsync)) with no lock): this test failed, observing multiple
    // independent fan-outs (all four counters landed in the double digits, not 1) — confirming
    // it actually catches the defect the double-checked-lock fix addresses, not just the
    // memoize-only shape the sequential test above already covers.
    [Fact]
    public async Task GetAsync_NConcurrentCallsOnColdCache_FansOutExactlyOnce()
    {
        const int concurrentCallers = 32;
        var counters = new CallCounters();
        var releaseGate = new TaskCompletionSource();
        var cache = BuildCache(counters, releaseGate.Task);
        using var barrier = new Barrier(concurrentCallers);

        var callerTasks = Enumerable.Range(0, concurrentCallers)
            .Select(_ => Task.Run(async () =>
            {
                barrier.SignalAndWait();
                await cache.GetAsync();
            }))
            .ToArray();

        // Give every real thread a chance to actually reach (and race on) GetAsync — and, on a
        // non-single-flighting implementation, to have already incremented the counters — before
        // releasing the gate that lets any in-flight fan-out complete.
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        releaseGate.SetResult();

        await Task.WhenAll(callerTasks);

        counters.Postgres.Should().Be(1);
        counters.StarRocks.Should().Be(1);
        counters.Qdrant.Should().Be(1);
        counters.Kafka.Should().Be(1);
    }

    // Regression test for the review finding that an unwrapped IEngagementStoreHealthCheck
    // failure would fault the Lazy<Task<HealthChecks>> the cache holds, and every caller for the
    // rest of the 5s TTL window would then have that same exception rethrown at them (a bare
    // 500) instead of a normal 503-with-checks-payload reading. FanOutAsync now wraps srTask the
    // same way as the other three checks, classifying a faulted CheckHealthAsync as Unhealthy
    // rather than letting the fault propagate.
    [Fact]
    public async Task GetAsync_StarRocksCheckFaults_ReturnsUnhealthyInsteadOfThrowing()
    {
        var counters = new CallCounters();
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var options = Options.Create(new EngagementStoreOptions { Enabled = true });
        var cache = new HealthCheckCache(
            new CountingRecordStoreQueryExecutor(counters, gate: null),
            new FaultingEngagementStoreHealthCheck(),
            new CountingVectorSchemaManager(counters, gate: null),
            new CountingEventProducer(counters, gate: null),
            options,
            memoryCache);

        var first = await cache.GetAsync();
        first.StarRocks.Should().Be(EngagementHealthStatus.Unhealthy);
        first.Postgres.Should().BeTrue();

        // The faulted-and-classified result must itself be the thing that gets cached and
        // re-served for the rest of the window — not a faulted Task that rethrows on every
        // subsequent caller.
        var second = await cache.GetAsync();
        second.StarRocks.Should().Be(EngagementHealthStatus.Unhealthy);
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

    private sealed class FaultingEngagementStoreHealthCheck : IEngagementStoreHealthCheck
    {
        public Task<EngagementHealthStatus> CheckHealthAsync() =>
            Task.FromException<EngagementHealthStatus>(new InvalidOperationException("simulated StarRocks health-check failure"));

        public Task<bool> IsHealthyAsync() =>
            throw new NotSupportedException("Not exercised by /health.");
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
