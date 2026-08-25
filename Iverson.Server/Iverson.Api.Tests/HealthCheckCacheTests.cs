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
    // This version releases 32 callers together per round and needs them to actually be
    // concurrent at the OS level for the race window to open. A second review finding caught the
    // first fix of this test: it used Task.Run (thread-pool threads) for the 32 workers, each
    // blocking inside barrier.SignalAndWait(). With the pool's default min-worker-thread count
    // equal to the processor count, the pool has to INJECT roughly 24-28 additional threads at
    // its starvation-detection rate (~1 every 500ms) before the barrier can trip — 12-15s. Two
    // consequences: the 200ms Task.Delay below almost always fired before any caller even
    // reached GetAsync (so the release-gate's job of holding the winner's fan-out open across
    // the race window was never actually exercised), and blocking 32 pool threads for 12-15s
    // each starved xunit's own parallel test collections, more than doubling the whole suite's
    // wall-clock time.
    //
    // Using dedicated background threads (not Task.Run) removes the pool dependency: the OS can
    // schedule 32 real threads onto the barrier immediately, no starvation-detection ramp
    // involved, so the barrier trips in microseconds. That fix alone, however, turned out to
    // trade thread-pool-starvation slowness for a DIFFERENT reliability problem, found while
    // verifying this test still fails against the pre-fix GetOrCreate implementation as
    // instructed: the actual defect window inside GetOrCreate (TryGetValue -> CreateEntry ->
    // run factory -> publish) is a few hundred nanoseconds of pure in-memory code with no I/O
    // yield point, and this sandbox has only 4 logical processors. A single simultaneous release
    // of 32 threads across 4 cores does not reliably land two threads inside that sub-microsecond
    // window every time — across 13 verification runs against the broken implementation with a
    // single round, it failed (correctly) ~65% of the time and false-passed ~35% of the time
    // (fan-out counts observed on the failing runs: 5, 11, 19, 22 — see the fix-round-3 report
    // entry for the full log). A test that only catches the bug roughly two times in three is not
    // acceptable regression coverage.
    //
    // The fix keeps exactly the structure the coordinator specified — Barrier(32),
    // .SignalAndWait() immediately before GetAsync(), dedicated non-pool threads — but repeats
    // that independent 32-thread race five times (fresh cache/counters/threads each round, so
    // each round is a genuinely cold cache again) and fails if ANY round shows more than one
    // fan-out. Five independent ~65%-detection rounds compound to a >99.4% chance of catching a
    // regression (1 - 0.35^5), while keeping the whole test well under a second per round —
    // cheaper than the single 12-15s Task.Run-based round it replaced twice over.
    //
    // Verified against the pre-fix HealthCheckCache (cache.GetOrCreate(key, entry => new
    // Lazy<Task<HealthChecks>>(FanOutAsync)) with no lock), 5-round version, 8 consecutive runs:
    // failed every time (round 1 alone was always enough to catch it in this sample, since a
    // false-pass on round 1 still gets caught by rounds 2-5) — see the fix-round-3 report entry.
    [Fact]
    public async Task GetAsync_NConcurrentCallsOnColdCache_FansOutExactlyOnce()
    {
        const int concurrentCallers = 32;
        const int rounds = 5;

        for (var round = 0; round < rounds; round++)
        {
            var counters = new CallCounters();
            var releaseGate = new TaskCompletionSource();
            var cache = BuildCache(counters, releaseGate.Task);
            using var barrier = new Barrier(concurrentCallers);
            var exceptions = new Exception?[concurrentCallers];

            var threads = new Thread[concurrentCallers];
            for (var i = 0; i < concurrentCallers; i++)
            {
                var index = i;
                threads[i] = new Thread(() =>
                {
                    try
                    {
                        barrier.SignalAndWait();
                        cache.GetAsync().GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        exceptions[index] = ex;
                    }
                })
                {
                    IsBackground = true
                };
            }

            foreach (var thread in threads)
                thread.Start();

            // Give every dedicated thread a chance to actually reach (and race on) GetAsync —
            // and, on a non-single-flighting implementation, to have already incremented the
            // counters — before releasing the gate that lets any in-flight fan-out complete.
            // With real threads (not pool threads) this window is dominated by OS scheduling,
            // not thread-pool injection, so 200ms is ample rather than a race against a 12-15s
            // ramp.
            await Task.Delay(TimeSpan.FromMilliseconds(200));
            releaseGate.SetResult();

            foreach (var thread in threads)
                thread.Join();

            exceptions.Should().OnlyContain(ex => ex == null, $"round {round}");

            counters.Postgres.Should().Be(1, $"round {round}");
            counters.StarRocks.Should().Be(1, $"round {round}");
            counters.Qdrant.Should().Be(1, $"round {round}");
            counters.Kafka.Should().Be(1, $"round {round}");
        }
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
