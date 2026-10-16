using System.Runtime.CompilerServices;
using System.Threading;
using FluentAssertions;
using Iverson.StarRocks;
using Xunit;

namespace Iverson.StarRocks.Tests;

public class StarRocksReadinessGateTests
{
    [Fact]
    public async Task EnsureReadyAsync_ReturnsImmediately_AfterFirstSuccess()
    {
        var callCount = 0;
        var gate = new StarRocksReadinessGate(
            checkAliveOnceAsync: _ => { Interlocked.Increment(ref callCount); return Task.FromResult(true); },
            timeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(10));

        await gate.EnsureReadyAsync();
        await gate.EnsureReadyAsync();
        await gate.EnsureReadyAsync();

        callCount.Should().Be(1);
    }

    [Fact]
    public async Task EnsureReadyAsync_ConcurrentCallers_ShareOneWait()
    {
        var callCount = 0;
        var gate = new StarRocksReadinessGate(
            checkAliveOnceAsync: _ =>
            {
                var n = Interlocked.Increment(ref callCount);
                return Task.FromResult(n >= 3); // not-ready, not-ready, ready
            },
            timeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(20));

        var callers = Enumerable.Range(0, 5).Select(_ => gate.EnsureReadyAsync()).ToArray();
        await Task.WhenAll(callers);

        callCount.Should().Be(3, "5 concurrent callers should share one in-flight wait, not each poll independently");
    }

    [Fact]
    public async Task EnsureReadyAsync_TimesOut_ThrowsAndResetsForNextCall()
    {
        var alwaysReady = false;
        var gate = new StarRocksReadinessGate(
            checkAliveOnceAsync: _ => Task.FromResult(alwaysReady),
            timeout: TimeSpan.FromMilliseconds(60),
            pollInterval: TimeSpan.FromMilliseconds(10));

        var act = async () => await gate.EnsureReadyAsync();
        await act.Should().ThrowAsync<EngagementNotReadyException>();

        alwaysReady = true;
        await gate.EnsureReadyAsync(); // should not throw — the gate must retry fresh, not stay wedged
    }

    [Fact]
    public async Task EnsureReadyAsync_ExceptionFromCheckDelegate_IsTreatedAsNotYetReady()
    {
        var attempt = 0;
        var gate = new StarRocksReadinessGate(
            checkAliveOnceAsync: _ =>
            {
                attempt++;
                if (attempt < 3) throw new InvalidOperationException("connection refused");
                return Task.FromResult(true);
            },
            timeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(10));

        await gate.EnsureReadyAsync();

        attempt.Should().Be(3);
    }

    [Fact]
    public async Task A_shared_wait_its_only_waiter_abandoned_is_still_observed_when_it_fails()
    {
        // RunAsync stops waiting on the gate with WaitAsync(ct); if that caller was the only waiter, nothing awaits
        // the shared wait's failure. It must not surface as an UnobservedTaskException once the task is collected.
        var marker = "gate-" + Guid.NewGuid().ToString("N");
        var unobserved = 0;
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) =>
        {
            if (e.Exception.InnerExceptions.Any(x => x is EngagementNotReadyException { InnerException.Message: var m } && m == marker))
                Interlocked.Increment(ref unobserved);
        };
        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            var wait = AbandonTheOnlyWaiter(marker);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!HasFaulted(wait) && !IsCollected(wait) && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            // A GC between the fault and the next poll leaves nothing to inspect. Only a finished task can be
            // collected (a pending one is rooted by the gate's poll timer), and its check always throws, so a
            // collected task faulted.
            (HasFaulted(wait) || IsCollected(wait)).Should().BeTrue("the gate gives up after its timeout");

            // The task reports itself faulted before the thread completing it has let go of it, and the
            // gate's own continuation may still be running. So it can be unreachable only a moment later:
            // collect repeatedly, within a bound, instead of once.
            var collectDeadline = DateTime.UtcNow.AddSeconds(5);
            while (!IsCollected(wait) && DateTime.UtcNow < collectDeadline)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(10);
            }
            IsCollected(wait).Should().BeTrue("otherwise this test proves nothing about observation");
            GC.Collect();
            GC.WaitForPendingFinalizers();

            unobserved.Should().Be(0);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }

    // Separate, non-inlined frames, so no local of the test method keeps the gate's task reachable.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<Task> AbandonTheOnlyWaiter(string marker)
    {
        var gate = new StarRocksReadinessGate(
            checkAliveOnceAsync: _ => throw new InvalidOperationException(marker),
            timeout: TimeSpan.FromMilliseconds(100),
            pollInterval: TimeSpan.FromMilliseconds(10));
        using var cts = new CancellationTokenSource();
        var shared = gate.EnsureReadyAsync();
        _ = shared.WaitAsync(cts.Token);
        cts.Cancel();                                                   // the only waiter stops waiting
        return new WeakReference<Task>(shared);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool HasFaulted(WeakReference<Task> wait) => wait.TryGetTarget(out var t) && t.IsFaulted;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsCollected(WeakReference<Task> wait) => !wait.TryGetTarget(out _);
}
