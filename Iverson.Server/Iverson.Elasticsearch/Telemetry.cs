using System.Diagnostics;

namespace Iverson.Elasticsearch;

internal static class Telemetry
{
    internal const string SourceName = "Iverson.Elasticsearch";
    internal static readonly ActivitySource Source = new(SourceName, "1.0.0");
}
