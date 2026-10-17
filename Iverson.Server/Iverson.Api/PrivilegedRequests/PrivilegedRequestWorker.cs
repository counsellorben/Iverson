using System.Collections.Concurrent;
using Grpc.Core;
using Iverson.Api.Consumers;
using Iverson.Sql;

namespace Iverson.Api.PrivilegedRequests;

/// <summary>
/// Runs the privileged requests the api queues (CSR round 12 §5.1), in the worker role only. Every
/// second it lists pending, unexpired requests, oldest first, and claims each with a conditional
/// UPDATE, so two worker replicas never run the same one. Each claimed request runs on its own
/// task while the loop keeps claiming; what serialises them is only the executor's advisory locks.
/// Every hour it fails requests still 'running' more than an hour after their claim, and deletes
/// finished requests older than a day.
/// </summary>
internal sealed class PrivilegedRequestWorker(
    IPrivilegedRequestRepository requests,
    IPrivilegedRequestExecutor executor,
    ILogger<PrivilegedRequestWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval      = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CleanupInterval   = TimeSpan.FromHours(1);
    private static readonly TimeSpan FinishedRetention = TimeSpan.FromDays(1);
    private const int ClaimBatchSize = 16;

    // A claim this old belongs to a worker that stopped mid-request: no caller waits longer than
    // 60 s. Without this the row would stay 'running' for good, because reconcile and replay
    // requests never expire. Unavailable is the code the executor records when the worker stops.
    private static readonly TimeSpan AbandonedAfter = TimeSpan.FromHours(1);
    private static readonly PrivilegedRequestResult Abandoned =
        new(StatusCode.Unavailable, "The worker stopped before finishing this request.");

    private readonly ConcurrentDictionary<Guid, Task> _inFlight = new();
    private DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await ConsumerResilience.RunWithRestartAsync(() => PollLoopAsync(ct), logger, "PrivilegedRequests", ct);

        // Each claimed request records its own outcome. Let the ones still running do so before the
        // host stops; they see the same token, so a cancellable one ends promptly.
        await WhenIdleAsync();
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await PollOnceAsync(ct);
            await Task.Delay(PollInterval, ct);
        }
    }

    internal async Task PollOnceAsync(CancellationToken ct)
    {
        foreach (var (id, task) in _inFlight)
            if (task.IsCompleted)
                _inFlight.TryRemove(id, out _);

        if (DateTimeOffset.UtcNow - _lastCleanup >= CleanupInterval)
        {
            await requests.FailAbandonedAsync(AbandonedAfter, Abandoned.ToJson());
            await requests.DeleteFinishedOlderThanAsync(FinishedRetention);
            _lastCleanup = DateTimeOffset.UtcNow;
        }

        foreach (var request in await requests.ListClaimableAsync(ClaimBatchSize))
        {
            if (!await requests.TryClaimAsync(request.Id))
                continue;

            _inFlight[request.Id] = Task.Run(() => RunClaimedAsync(request, ct), CancellationToken.None);
        }
    }

    internal Task WhenIdleAsync() => Task.WhenAll(_inFlight.Values);

    private async Task RunClaimedAsync(PrivilegedRequestRow request, CancellationToken ct)
    {
        var result = await executor.RunAsync(request, ct);
        try
        {
            await requests.CompleteAsync(
                request.Id,
                result.Code == StatusCode.OK ? PrivilegedRequestStatus.Succeeded : PrivilegedRequestStatus.Failed,
                result.ToJson());
        }
        catch (Exception ex)
        {
            // The request ran; only recording it failed. Its row stays 'running', so its caller
            // gets DeadlineExceeded (RegisterSchema) or 202 (the admin routes).
            logger.LogError(ex, "Privileged request {RequestId} ran, but its outcome could not be recorded", request.Id);
        }
    }
}
