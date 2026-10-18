using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Grpc.Core;
using Iverson.Api.PrivilegedRequests;
using Iverson.Api.Schema;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.PrivilegedRequests;

/// <summary>
/// CSR round 12 §5.1: the worker claims each pending request with a conditional UPDATE, runs every
/// claimed request on its own task while it keeps claiming, records the outcome, and deletes
/// finished requests older than a day.
/// </summary>
public class PrivilegedRequestWorkerTests
{
    private readonly IPrivilegedRequestRepository _requests = Substitute.For<IPrivilegedRequestRepository>();
    private readonly IPrivilegedRequestExecutor _executor = Substitute.For<IPrivilegedRequestExecutor>();
    private readonly ConcurrentDictionary<Guid, (string Status, string ResultJson)> _completed = new();
    private readonly PrivilegedRequestWorker _sut;

    public PrivilegedRequestWorkerTests()
    {
        _requests.TryClaimAsync(Arg.Any<Guid>()).Returns(true);
        _requests.CompleteAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => _completed[(Guid)ci[0]] = ((string)ci[1], (string)ci[2]));
        _executor.RunAsync(Arg.Any<PrivilegedRequestRow>(), Arg.Any<CancellationToken>()).Returns(PrivilegedRequestResult.Ok());
        _sut = new PrivilegedRequestWorker(_requests, _executor, NullLogger<PrivilegedRequestWorker>.Instance);
    }

    private static PrivilegedRequestRow Pending() =>
        new(Guid.NewGuid(), PrivilegedRequestKinds.RegisterSchema, "{}", PrivilegedRequestStatus.Pending, null);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition did not hold within 5 s.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task PollOnce_RunsOnlyTheRequestsItClaims()
    {
        var won = Pending();
        var lost = Pending();
        _requests.ListClaimableAsync(Arg.Any<int>()).Returns([won, lost]);
        _requests.TryClaimAsync(lost.Id).Returns(false);

        await _sut.PollOnceAsync(CancellationToken.None);
        await _sut.WhenIdleAsync();

        await _executor.Received(1).RunAsync(Arg.Is(won), Arg.Any<CancellationToken>());
        await _executor.DidNotReceive().RunAsync(Arg.Is(lost), Arg.Any<CancellationToken>());
        _completed[won.Id].Status.Should().Be(PrivilegedRequestStatus.Succeeded);
        _completed.ContainsKey(lost.Id).Should().BeFalse();
    }

    [Fact]
    public async Task PollOnce_DoesNotWaitForEarlierRequestsToFinish()
    {
        var slow = Pending();
        var fast = Pending();
        var gate = new TaskCompletionSource<PrivilegedRequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests.ListClaimableAsync(Arg.Any<int>()).Returns([slow, fast]);
        _executor.RunAsync(Arg.Is(slow), Arg.Any<CancellationToken>()).Returns(gate.Task);

        await _sut.PollOnceAsync(CancellationToken.None);
        await WaitUntilAsync(() => _completed.ContainsKey(fast.Id));

        _completed.ContainsKey(slow.Id).Should().BeFalse("the slow request is still running");
        gate.SetResult(PrivilegedRequestResult.Ok());
        await _sut.WhenIdleAsync();
        _completed.ContainsKey(slow.Id).Should().BeTrue();
    }

    [Fact]
    public async Task AFailedOutcome_IsRecordedAsFailed_WithItsResult()
    {
        var request = Pending();
        _requests.ListClaimableAsync(Arg.Any<int>()).Returns([request]);
        _executor.RunAsync(Arg.Is(request), Arg.Any<CancellationToken>())
            .Returns(new PrivilegedRequestResult(StatusCode.PermissionDenied, "denied"));

        await _sut.PollOnceAsync(CancellationToken.None);
        await _sut.WhenIdleAsync();

        var (status, resultJson) = _completed[request.Id];
        status.Should().Be(PrivilegedRequestStatus.Failed);
        JsonSerializer.Deserialize<PrivilegedRequestResult>(resultJson, SchemaRegistry.JsonOptions)
            .Should().Be(new PrivilegedRequestResult(StatusCode.PermissionDenied, "denied"));
    }

    [Fact]
    public async Task PollOnce_DeletesFinishedRequestsOlderThanADay_AtMostOncePerInterval()
    {
        _requests.ListClaimableAsync(Arg.Any<int>()).Returns(Enumerable.Empty<PrivilegedRequestRow>());

        await _sut.PollOnceAsync(CancellationToken.None);
        await _sut.PollOnceAsync(CancellationToken.None);

        await _requests.Received(1).DeleteFinishedOlderThanAsync(TimeSpan.FromDays(1));
    }

    [Fact]
    public async Task PollOnce_FailsRequestsClaimedOverAnHourAgo_AsUnavailable_AtMostOncePerInterval()
    {
        // A worker that stops mid-request leaves its row 'running'. Reconcile and replay requests
        // never expire, so this is the only thing that ends them.
        _requests.ListClaimableAsync(Arg.Any<int>()).Returns(Enumerable.Empty<PrivilegedRequestRow>());

        await _sut.PollOnceAsync(CancellationToken.None);
        await _sut.PollOnceAsync(CancellationToken.None);

        await _requests.Received(1).FailAbandonedAsync(TimeSpan.FromHours(1), Arg.Any<string>());
        var resultJson = (string)_requests.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IPrivilegedRequestRepository.FailAbandonedAsync))
            .GetArguments()[1]!;
        JsonSerializer.Deserialize<PrivilegedRequestResult>(resultJson, SchemaRegistry.JsonOptions)
            .Should().Be(new PrivilegedRequestResult(StatusCode.Unavailable, "The worker stopped before finishing this request."));
    }

    [Fact]
    public async Task Stopping_WaitsForTheRequestsStillRunning_ToRecordTheirOutcome()
    {
        var request = Pending();
        var gate = new TaskCompletionSource<PrivilegedRequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests.ListClaimableAsync(Arg.Any<int>()).Returns([request], []);
        _executor.RunAsync(Arg.Is(request), Arg.Any<CancellationToken>()).Returns(gate.Task);

        await _sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _executor.ReceivedCalls().Any());

        var stopping = _sut.StopAsync(CancellationToken.None);
        await Task.Delay(300);
        stopping.IsCompleted.Should().BeFalse("the claimed request has not finished, so the host must not stop yet");
        _completed.ContainsKey(request.Id).Should().BeFalse();

        gate.SetResult(PrivilegedRequestResult.Ok());
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        _completed[request.Id].Status.Should().Be(PrivilegedRequestStatus.Succeeded);
    }
}
