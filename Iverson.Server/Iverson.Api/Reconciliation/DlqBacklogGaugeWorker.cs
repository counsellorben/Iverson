using Iverson.Api.Consumers;
using Iverson.Sql;

namespace Iverson.Api.Reconciliation;

/// <summary>
/// DlqMonitorConsumer has no periodic loop of its own (it's a pure Kafka consumer, reacting only
/// to newly-routed DLQ messages) — this is the sibling loop that periodically refreshes
/// dlq.unreplayed_count so the gauge reflects the DLQ table's true backlog even when nothing new
/// is currently failing.
/// </summary>
internal sealed class DlqBacklogGaugeWorker(
    IDlqRepository dlq,
    ILogger<DlqBacklogGaugeWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    protected override Task ExecuteAsync(CancellationToken ct) =>
        ConsumerResilience.RunWithRestartAsync(
            () => PollLoopAsync(ct),
            logger,
            "DlqBacklogGauge",
            ct);

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ReconciliationTelemetry.DlqUnreplayedCount = await dlq.CountUnreplayedAsync();
            await Task.Delay(PollInterval, ct);
        }
    }
}
