using System.Diagnostics.Metrics;

namespace Iverson.Api.Reconciliation;

/// <summary>
/// Backlog-depth gauges for the two "is fan-out silently falling behind?" signals this project
/// otherwise has no visibility into: the reconciliation outbox queue and the DLQ table. Both
/// fields are refreshed on a 30s poll cadence by <see cref="ReconciliationQueueWorker"/> and
/// <see cref="DlqBacklogGaugeWorker"/> respectively — ObservableGauge reads whatever value is
/// currently here whenever the OTel SDK collects, so no locking is needed beyond `volatile`.
/// </summary>
internal static class ReconciliationTelemetry
{
    internal const string MeterName = "Iverson.Api.Reconciliation";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    internal static volatile int ReconciliationQueueDepth;
    internal static volatile int DlqUnreplayedCount;

    static ReconciliationTelemetry()
    {
        Meter.CreateObservableGauge(
            "reconciliation.queue_depth",
            () => ReconciliationQueueDepth,
            description: "Pending rows in the reconciliation outbox queue");

        Meter.CreateObservableGauge(
            "dlq.unreplayed_count",
            () => DlqUnreplayedCount,
            description: "Unreplayed rows in the dead-letter queue table");
    }
}
