using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Iverson.Events;

internal static class Telemetry
{
    internal const string SourceName = "Iverson.Events";
    internal static readonly ActivitySource Source = new(SourceName, "1.0.0");

    internal static readonly Meter Meter = new(SourceName, "1.0.0");

    internal static readonly Counter<long> ConsumerRetries =
        Meter.CreateCounter<long>("consumer.retries", description: "Projection handler retry attempts");

    internal static readonly Counter<long> ConsumerDlqRouted =
        Meter.CreateCounter<long>("consumer.dlq_routed", description: "Messages routed to the dead-letter queue");
}
