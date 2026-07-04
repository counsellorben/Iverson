using System.Reflection;
using FluentAssertions;
using Iverson.StarRocks;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
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

    // MySqlException's constructors are internal to MySqlConnector with no InternalsVisibleTo
    // grant to this assembly, so a transient/non-transient instance must be built via reflection
    // against the exact 4-arg overload to avoid AmbiguousMatchException against sibling overloads.
    private static MySqlException CreateMySqlException(MySqlErrorCode errorCode, string message = "test") =>
        (MySqlException)typeof(MySqlException)
            .GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null,
                types: [typeof(MySqlErrorCode), typeof(string), typeof(string), typeof(Exception)],
                modifiers: null)!
            .Invoke([errorCode, null, message, null]);

    private static MySqlException TransientException() =>
        CreateMySqlException(MySqlErrorCode.UnableToConnectToHost, "Unable to connect to any of the specified MySQL hosts.");

    private static MySqlException NonTransientException() =>
        CreateMySqlException(MySqlErrorCode.ParseError, "You have an error in your SQL syntax");

    [Fact]
    public async Task Build_AbsorbsATransientFailure_ThenSucceeds()
    {
        var pipeline = StarRocksResiliencePipelineFactory.Build(FastTestOptions(), NullLogger.Instance);
        var attempt = 0;

        var result = await pipeline.ExecuteAsync(async _ =>
        {
            attempt++;
            if (attempt == 1) throw TransientException();
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
            throw TransientException();
        }).AsTask();

        await Assert.ThrowsAsync<MySqlException>(failingCall); // call 1
        await Assert.ThrowsAsync<MySqlException>(failingCall); // call 2 -> trips breaker

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
            if (shouldFail) throw TransientException();
            return 1;
        }).AsTask();

        await Assert.ThrowsAsync<MySqlException>(call);         // call 1
        await Assert.ThrowsAsync<MySqlException>(call);         // call 2 -> trips breaker
        await Assert.ThrowsAsync<BrokenCircuitException>(call); // call 3 -> fails fast

        await Task.Delay(TimeSpan.FromMilliseconds(700)); // exceed BreakDuration
        shouldFail = false;

        var result = await pipeline.ExecuteAsync(async _ => { await Task.Delay(1); return 99; });

        result.Should().Be(99, "the half-open trial call should succeed and close the circuit");
    }

    [Fact]
    public async Task Build_DoesNotRetryOrTripBreaker_OnNonTransientApplicationError()
    {
        var pipeline = StarRocksResiliencePipelineFactory.Build(FastTestOptions(), NullLogger.Instance);
        var attempts = 0;

        Func<Task> call = () => pipeline.ExecuteAsync<int>(async _ =>
        {
            attempts++;
            throw NonTransientException();
        }).AsTask();

        // Same call repeated MinimumThroughput times: if ShouldHandle wrongly counted this as
        // handled, the breaker would trip on the 2nd call, per FastTestOptions (MinimumThroughput = 2).
        await Assert.ThrowsAsync<MySqlException>(call);
        await Assert.ThrowsAsync<MySqlException>(call);
        await Assert.ThrowsAsync<MySqlException>(call);

        attempts.Should().Be(3, "a non-transient application error must never be retried");

        // A 4th call must still reach the operation (not BrokenCircuitException) — proving the
        // breaker never tripped on these non-transient failures.
        await Assert.ThrowsAsync<MySqlException>(call);
        attempts.Should().Be(4, "the circuit must never open for exceptions ShouldHandle excludes");
    }

    [Fact]
    public async Task Build_DoesNotRetryOrTripBreaker_OnNonMySqlException()
    {
        var pipeline = StarRocksResiliencePipelineFactory.Build(FastTestOptions(), NullLogger.Instance);
        var attempts = 0;

        Func<Task> call = () => pipeline.ExecuteAsync<int>(async _ =>
        {
            attempts++;
            throw new InvalidOperationException("not a StarRocks fault");
        }).AsTask();

        await Assert.ThrowsAsync<InvalidOperationException>(call);
        await Assert.ThrowsAsync<InvalidOperationException>(call);

        attempts.Should().Be(2, "exceptions that aren't MySqlException must never be retried or trip the breaker");
    }
}
