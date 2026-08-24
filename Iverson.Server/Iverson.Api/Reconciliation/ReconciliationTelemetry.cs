using System.Diagnostics.Metrics;

namespace Iverson.Api.Reconciliation;

/// <summary>
/// Backlog-depth gauges for the "is fan-out silently falling behind?" signals this project
/// otherwise has no visibility into: the reconciliation outbox queue, the DLQ table, and the
/// document re-render queue. Each field is refreshed on its worker's own poll cadence by
/// <see cref="ReconciliationQueueWorker"/>, <see cref="DlqBacklogGaugeWorker"/>, and
/// <see cref="DocumentRerenderQueueWorker"/> respectively — ObservableGauge reads whatever value
/// is currently here whenever the OTel SDK collects, so no locking is needed beyond `volatile`.
/// </summary>
internal static class ReconciliationTelemetry
{
    internal const string MeterName = "Iverson.Api.Reconciliation";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    internal static volatile int ReconciliationQueueDepth;
    internal static volatile int DlqUnreplayedCount;
    internal static volatile int DocumentRerenderQueueDepth;

    static ReconciliationTelemetry()
    {
        Meter.CreateObservableGauge(
            "reconciliation.queue_depth",
            () =>
                ReconciliationQueueDepth,
                description: "Pending rows in the reconciliation outbox queue");

        Meter.CreateObservableGauge(
            "dlq.unreplayed_count",
            () =>
                DlqUnreplayedCount,
                description: "Unreplayed rows in the dead-letter queue table");

        Meter.CreateObservableGauge(
            "document_rerender.queue_depth",
            () =>
                DocumentRerenderQueueDepth,
                description: "Pending rows in the document re-render queue");
    }
}
