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

    [Fact]
    public async Task NonTransient_CheckExceedsBound_CancelsTheTokenItWasGiven()
    {
        CancellationToken captured = default;
        // Ignores its token and captures it; only the bound can end the wait.
        var check = new FakeCheck(async ct =>
        {
            captured = ct;
            await Task.Delay(TimeSpan.FromSeconds(30));
            return true;
        });

        await EngagementRepository.RewrapIfBackendUnavailableAsync(
            NonTransient(), check.Invoke, TimeSpan.FromMilliseconds(50));

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
