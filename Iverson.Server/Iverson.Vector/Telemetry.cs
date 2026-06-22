using System.Diagnostics;

namespace Iverson.Vector;

internal static class Telemetry
{
    internal const string SourceName = "Iverson.Vector";
    internal static readonly ActivitySource Source = new(SourceName, "1.0.0");
}
