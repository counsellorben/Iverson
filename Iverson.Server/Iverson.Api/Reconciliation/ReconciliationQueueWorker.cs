using Iverson.Api.Consumers;

namespace Iverson.Api.Reconciliation;

internal sealed class ReconciliationQueueWorker(
    ReconciliationService reconciliation,
    ILogger<ReconciliationQueueWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    protected override Task ExecuteAsync(CancellationToken ct) =>
        ConsumerResilience.RunWithRestartAsync(
            () => PollLoopAsync(ct),
            logger,
            "ReconciliationQueue",
            ct);

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await reconciliation.ProcessQueuedFailuresAsync(ct);
            ReconciliationTelemetry.ReconciliationQueueDepth = await reconciliation.CountPendingAsync();
            await Task.Delay(PollInterval, ct);
        }
    }
}
