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
        await act.Should().ThrowAsync<StarRocksNotReadyException>();

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
}
