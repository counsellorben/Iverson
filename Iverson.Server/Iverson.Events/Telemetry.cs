using System.Diagnostics;

namespace Iverson.Events;

internal static class Telemetry
{
    internal const string SourceName = "Iverson.Events";
    internal static readonly ActivitySource Source = new(SourceName, "1.0.0");
}
