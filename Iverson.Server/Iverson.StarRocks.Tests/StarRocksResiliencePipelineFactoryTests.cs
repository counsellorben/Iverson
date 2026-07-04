using FluentAssertions;
using Iverson.StarRocks;
using Microsoft.Extensions.Logging.Abstractions;
using Polly.CircuitBreaker;
using Xunit;

namespace Iverson.StarRocks.Tests;

public class StarRocksResiliencePipelineFactoryTests
{
    private static StarRocksCircuitBreakerOptions FastTestOptions() => new()
    {
        FailureRatio      = 1.0,
        MinimumThroughput = 2,
        SamplingDuration  = TimeSpan.FromSeconds(5),
        BreakDuration     = TimeSpan.FromMilliseconds(600)
    };

    [Fact]
    public async Task Build_AbsorbsATransientFailure_ThenSucceeds()
    {
        var pipeline = StarRocksResiliencePipelineFactory.Build(FastTestOptions(), NullLogger.Instance);
        var attempt = 0;

        var result = await pipeline.ExecuteAsync(async _ =>
        {
            attempt++;
            if (attempt == 1) throw new InvalidOperationException("transient");
            return 42;
        });

        result.Should().Be(42);
        attempt.Should().Be(2);
    }

    [Fact]
    public async Task Build_OpensCircuit_AfterFailureThreshold_AndFailsFastWithoutInvokingOperation()
    {
        var pipeline = StarRocksResiliencePipelineFactory.Build(FastTestOptions(), NullLogger.Instance);
        var attempts = 0;

        Func<Task> failingCall = () => pipeline.ExecuteAsync<int>(async _ =>
        {
            attempts++;
            throw new InvalidOperationException("down");
        }).AsTask();

        await Assert.ThrowsAsync<InvalidOperationException>(failingCall); // call 1
        await Assert.ThrowsAsync<InvalidOperationException>(failingCall); // call 2 -> trips breaker

        var attemptsBeforeOpen = attempts;
        await Assert.ThrowsAsync<BrokenCircuitException>(failingCall); // call 3 -> fails fast

        attempts.Should().Be(attemptsBeforeOpen, "an open circuit must not invoke the underlying operation at all");
    }

    [Fact]
    public async Task Build_HalfOpenTrial_ClosesCircuitOnSuccess_AfterBreakDurationElapses()
    {
        var pipeline = StarRocksResiliencePipelineFactory.Build(FastTestOptions(), NullLogger.Instance);
        var shouldFail = true;

        Func<Task> call = () => pipeline.ExecuteAsync<int>(async _ =>
        {
            if (shouldFail) throw new InvalidOperationException("down");
            return 1;
        }).AsTask();

        await Assert.ThrowsAsync<InvalidOperationException>(call); // call 1
        await Assert.ThrowsAsync<InvalidOperationException>(call); // call 2 -> trips breaker
        await Assert.ThrowsAsync<BrokenCircuitException>(call);    // call 3 -> fails fast

        await Task.Delay(TimeSpan.FromMilliseconds(700)); // exceed BreakDuration
        shouldFail = false;

        var result = await pipeline.ExecuteAsync(async _ => { await Task.Delay(1); return 99; });

        result.Should().Be(99, "the half-open trial call should succeed and close the circuit");
    }
}
