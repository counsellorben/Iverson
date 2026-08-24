using FluentAssertions;
using Xunit;

namespace Iverson.ClientConformance.Tests;

/// <summary>
/// Unit coverage for the harness's one projection-wait convention. The properties pinned here are
/// the ones the convention exists to guarantee — it polls rather than sleeps, it stops rather than
/// hanging, a timeout is data rather than an exception, and a probe that throws does not end the
/// wait — so a later axis reusing <see cref="ProjectionWaiter"/> for a different store inherits
/// them instead of re-deciding them.
/// </summary>
public class ProjectionWaiterTests
{
    private static ProjectionWaiter Fast(double timeoutSeconds = 1) =>
        new(TimeSpan.FromSeconds(timeoutSeconds), TimeSpan.FromMilliseconds(10));

    [Fact]
    public async Task WaitAsync_ProbeSatisfiedImmediately_ReturnsAfterExactlyOneAttempt()
    {
        var result = await Fast().WaitAsync("rows", _ => Task.FromResult(ProbeOutcome.Ready("2 visible")));

        result.Satisfied.Should().BeTrue();
        result.Attempts.Should().Be(1, "the probe must not be preceded by a fixed sleep");
        result.LastDetail.Should().Be("2 visible");
    }

    [Fact]
    public async Task WaitAsync_ProbeSatisfiedImmediately_DoesNotWaitAnIntervalFirst()
    {
        // Pins "poll, never sleep-then-poll" as an observable property, not just as a comment.
        // Mutation testing found that moving the delay ahead of the probe left every other test in
        // this file green: attempt counts are identical either way, only the clock differs.
        var waiter = new ProjectionWaiter(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));

        var started = DateTime.UtcNow;
        var result = await waiter.WaitAsync("rows", _ => Task.FromResult(ProbeOutcome.Ready("visible")));
        var elapsed = DateTime.UtcNow - started;

        result.Satisfied.Should().BeTrue();
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "the first probe must precede any delay");
    }

    [Fact]
    public async Task WaitAsync_ProbeSatisfiedOnALaterAttempt_KeepsPollingUntilItIs()
    {
        var attempts = 0;
        var result = await Fast(5).WaitAsync("rows", _ =>
        {
            attempts++;
            return Task.FromResult(attempts < 3
                ? ProbeOutcome.NotYet($"{attempts} of 3")
                : ProbeOutcome.Ready("3 of 3"));
        });

        result.Satisfied.Should().BeTrue();
        result.Attempts.Should().Be(3);
    }

    [Fact]
    public async Task WaitAsync_ProbeNeverSatisfied_ReturnsAnUnsatisfiedResultRatherThanThrowingOrHanging()
    {
        var result = await Fast(0.2).WaitAsync("rows", _ => Task.FromResult(ProbeOutcome.NotYet("0 of 5")));

        result.Satisfied.Should().BeFalse();
        result.Attempts.Should().BeGreaterThan(1);
        result.LastDetail.Should().Be("0 of 5");
        result.TimeoutDetail.Should().Contain("rows").And.Contain("0 of 5")
            .And.Contain("not evidence about any client library");
    }

    [Fact]
    public async Task WaitAsync_ProbeThrows_IsCaughtAndCarriedIntoTheTimeoutDetail()
    {
        // A projection that is not ready yet legitimately surfaces as an error from the store — an
        // unknown table, a closed connection — so one throwing attempt must not end the wait.
        var attempts = 0;
        var result = await Fast(0.2).WaitAsync("rows", _ =>
        {
            attempts++;
            throw new InvalidOperationException("table not found");
        });

        result.Satisfied.Should().BeFalse();
        attempts.Should().BeGreaterThan(1);
        result.LastDetail.Should().Contain("table not found");
    }

    [Fact]
    public async Task WaitAsync_ProbeThrowsThenSucceeds_StillSatisfied()
    {
        var attempts = 0;
        var result = await Fast(5).WaitAsync("rows", _ =>
        {
            attempts++;
            if (attempts == 1) throw new InvalidOperationException("not ready");
            return Task.FromResult(ProbeOutcome.Ready("ready"));
        });

        result.Satisfied.Should().BeTrue();
        result.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task WaitAsync_ZeroTimeout_StillProbesOnce()
    {
        var attempts = 0;
        var result = await new ProjectionWaiter(TimeSpan.Zero, TimeSpan.Zero).WaitAsync("rows", _ =>
        {
            attempts++;
            return Task.FromResult(ProbeOutcome.NotYet("nothing"));
        });

        attempts.Should().Be(1, "a waiter configured down to nothing must still observe the store");
        result.Satisfied.Should().BeFalse();
    }

    [Fact]
    public async Task WaitAsync_CancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await Fast(5).WaitAsync(
            "rows", ct => { ct.ThrowIfCancellationRequested(); return Task.FromResult(ProbeOutcome.NotYet("x")); },
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task WaitAsync_CallerCancelsMidProbeAfterTheTimeoutElapsed_ThrowsRatherThanReportingUnsatisfied()
    {
        // The one case that separates caller cancellation from budget expiry: the cancellation
        // arrives INSIDE a probe at a moment when the timeout has already elapsed. Without the
        // `when (ct.IsCancellationRequested)` filter the throw is swallowed as an attempt-budget
        // expiry and the wait returns an unsatisfied result — an operator-visible report blaming
        // the outbox for what was really a shutdown. A shutdown must surface as a cancellation.
        using var cts = new CancellationTokenSource();

        var act = async () => await Fast(0).WaitAsync(
            "rows",
            async attemptToken =>
            {
                await cts.CancelAsync();
                attemptToken.ThrowIfCancellationRequested();
                return ProbeOutcome.NotYet("unreachable");
            },
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "a caller cancellation must never be reported as the projection failing to arrive");
    }

    [Fact]
    public async Task WaitAsync_ProbeOutlivesTheTimeout_ReturnsUnsatisfiedRatherThanHanging()
    {
        // The bound must apply INSIDE an attempt, not only between attempts: a probe that stalls
        // mid-call (an accepted gRPC call whose stream never completes) would otherwise block
        // attempt 1 forever and the timeout would never fire.
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await Fast(0.2).WaitAsync(
            "rows",
            async token => { await Task.Delay(Timeout.Infinite, token); return ProbeOutcome.NotYet("unreachable"); },
            safety.Token);

        result.Satisfied.Should().BeFalse();
        result.Attempts.Should().BeGreaterThan(0);
        safety.IsCancellationRequested.Should().BeFalse("the wait must return well inside its own bound");
    }

    [Fact]
    public async Task WaitAsync_ProbeOutlivesTheTimeout_DoesNotReportItAsCallerCancellation()
    {
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var act = async () => await Fast(0.2).WaitAsync(
            "rows",
            async token => { await Task.Delay(Timeout.Infinite, token); return ProbeOutcome.NotYet("x"); },
            safety.Token);

        await act.Should().NotThrowAsync("budget expiry is data, caller cancellation is the exception");
    }

    [Fact]
    public async Task WaitAsync_ZeroTimeout_GivesTheProbeAUsableBudgetToActuallyObserveTheStore()
    {
        // The companion to WaitAsync_ZeroTimeout_StillProbesOnce, whose probe IGNORES its token and
        // so cannot tell "the store was asked" from "the probe was handed an already-dead token and
        // returned instantly". A token-HONOURING probe can, and the class doc's claim that a waiter
        // configured down to nothing still observes the store is only true if this passes.
        var observedTheStore = false;
        var waiter = new ProjectionWaiter(TimeSpan.Zero, TimeSpan.FromSeconds(2));

        var result = await waiter.WaitAsync("rows", async token =>
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromMilliseconds(20), token);
            observedTheStore = true;
            return ProbeOutcome.NotYet("nothing");
        });

        observedTheStore.Should().BeTrue(
            "each attempt's budget is floored at Interval, so even a zero timeout buys one real read");
        result.Satisfied.Should().BeFalse();
        result.Attempts.Should().Be(1);
    }
}
