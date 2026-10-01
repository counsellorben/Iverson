using System.Reflection;
using FluentAssertions;
using Iverson.StarRocks;
using MySqlConnector;
using Xunit;

namespace Iverson.StarRocks.Tests;

/// <summary>
/// Spec §5: <see cref="EngagementRepository.RewrapIfBackendUnavailableAsync"/> turns a non-transient,
/// non-missing-resource StarRocks failure into <see cref="EngagementNotReadyException"/> when the backend cannot
/// be shown alive, driven here by a fake liveness check so no server is needed.
/// </summary>
public class EngagementRepositoryLivenessRewrapTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    // MySqlException's constructors are internal to MySqlConnector with no InternalsVisibleTo grant to this
    // assembly; built via reflection against the exact overload (StarRocksResiliencePipelineFactoryTests).
    private static MySqlException CreateMySqlException(MySqlErrorCode errorCode, string message) =>
        (MySqlException)typeof(MySqlException)
            .GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null,
                types: [typeof(MySqlErrorCode), typeof(string), typeof(string), typeof(Exception)],
                modifiers: null)!
            .Invoke([errorCode, null, message, null]);

    /// <summary>Shape 2 (frozen server): IsTransient is false, and it is not a missing resource.</summary>
    private static MySqlException NonTransient() =>
        CreateMySqlException(MySqlErrorCode.CommandTimeoutExpired, "The Command Timeout expired before the operation completed.");

    /// <summary>A liveness check that records whether it ran.</summary>
    private sealed class FakeCheck(Func<CancellationToken, Task<bool>> behaviour)
    {
        public int Calls { get; private set; }

        public Task<bool> Invoke(CancellationToken ct)
        {
            Calls++;
            return behaviour(ct);
        }
    }

    [Fact]
    public async Task NonTransient_BackendReportsDead_RewrapsAsNotReady()
    {
        var original = NonTransient();
        var check = new FakeCheck(_ => Task.FromResult(false));

        var result = await EngagementRepository.RewrapIfBackendUnavailableAsync(original, check.Invoke, Bound);

        result.Should().NotBeNull();
        result!.InnerException.Should().BeSameAs(original);
        check.Calls.Should().Be(1);
    }

    [Fact]
    public async Task NonTransient_CheckThrows_RewrapsAsNotReady()
    {
        var original = NonTransient();
        var check = new FakeCheck(_ => Task.FromException<bool>(new ProbeFailedException()));

        var result = await EngagementRepository.RewrapIfBackendUnavailableAsync(original, check.Invoke, Bound);

        result.Should().NotBeNull();
        result!.InnerException.Should().BeSameAs(original);
        check.Calls.Should().Be(1);
    }

    [Fact]
    public async Task NonTransient_CheckExceedsBound_RewrapsAsNotReady()
    {
        var original = NonTransient();
        // Ignores its token, so only the bound can end the wait.
        var check = new FakeCheck(async _ => { await Task.Delay(TimeSpan.FromSeconds(30)); return true; });

        var result = await EngagementRepository.RewrapIfBackendUnavailableAsync(
            original, check.Invoke, TimeSpan.FromMilliseconds(50));

        result.Should().NotBeNull();
        result!.InnerException.Should().BeSameAs(original);
    }

    /// <summary>
    /// A clock that only moves when told to. It captures the timer <c>WaitAsync</c> creates for the bound, so a
    /// test can elapse the bound at a moment of its choosing instead of racing a real timer.
    /// </summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            return new InertTimer();
        }

        public void ElapseBound() =>
            (_callback ?? throw new InvalidOperationException("No timer was created for the bound."))(_state);

        private sealed class InertTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task NonTransient_CheckExceedsBound_CancelsTheTokenItWasGiven()
    {
        // Deterministic: the bound elapses only when the test says so, and no other timer exists. A token that is
        // cancelled only by its own real timer, or never, stays uncancelled here and fails the assertion.
        var time = new ManualTimeProvider();
        var neverCompletes = new TaskCompletionSource<bool>();
        CancellationToken captured = default;
        var check = new FakeCheck(ct =>
        {
            captured = ct;   // ignores its token, like a probe stuck in the driver
            return neverCompletes.Task;
        });

        var pending = EngagementRepository.RewrapIfBackendUnavailableAsync(NonTransient(), check.Invoke, Bound, time);
        pending.IsCompleted.Should().BeFalse("the check has not finished and the bound has not elapsed");
        captured.IsCancellationRequested.Should().BeFalse();

        time.ElapseBound();
        var result = await pending;

        result.Should().NotBeNull("a check that outlives the bound means the backend cannot be shown alive");
        captured.CanBeCanceled.Should().BeTrue();
        captured.IsCancellationRequested.Should().BeTrue("the orphaned probe must be told to stop");
    }

    [Fact]
    public async Task NonTransient_CheckObservesTokenAtBound_RewrapsAsNotReady()
    {
        var original = NonTransient();
        var check = new FakeCheck(async ct => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return true; });

        var result = await EngagementRepository.RewrapIfBackendUnavailableAsync(
            original, check.Invoke, TimeSpan.FromMilliseconds(50));

        result.Should().NotBeNull();
        result!.InnerException.Should().BeSameAs(original);
    }

    [Fact]
    public async Task NonTransient_BackendReportsAlive_ReturnsNullSoTheOriginalIsRethrown()
    {
        var check = new FakeCheck(_ => Task.FromResult(true));

        var result = await EngagementRepository.RewrapIfBackendUnavailableAsync(NonTransient(), check.Invoke, Bound);

        result.Should().BeNull();
        check.Calls.Should().Be(1);
    }

    public static TheoryData<string> ExemptCases() => new(Exempt.Keys);

    private static readonly Dictionary<string, Func<Exception>> Exempt = new()
    {
        ["missing resource: cannot find role"]  = () => CreateMySqlException(MySqlErrorCode.ParseError, "cannot find role role_tenant_x"),
        ["missing resource: is not granted to"] = () => CreateMySqlException(MySqlErrorCode.ParseError, "Role role_tenant_x is not granted to 'iverson_app'@'%'"),
        ["missing resource: unknown table"]     = () => CreateMySqlException((MySqlErrorCode)5502, "Unknown table 'iverson.articles'"),
        ["library-transient MySqlException"]    = () => CreateMySqlException(MySqlErrorCode.UnableToConnectToHost, "Unable to connect to any of the specified MySQL hosts."),
        ["not a MySqlException"]                = () => new InvalidOperationException("boom"),
    };

    [Theory]
    [MemberData(nameof(ExemptCases))]
    public async Task Exempt_IsNotRewrapped_AndMakesNoLivenessCheck(string name)
    {
        var check = new FakeCheck(_ => Task.FromResult(false));   // would rewrap, if it were consulted

        var result = await EngagementRepository.RewrapIfBackendUnavailableAsync(Exempt[name](), check.Invoke, Bound);

        result.Should().BeNull(name);
        check.Calls.Should().Be(0, name);
    }

    private sealed class ProbeFailedException() : Exception("connection refused");
}
