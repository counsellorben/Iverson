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
}
