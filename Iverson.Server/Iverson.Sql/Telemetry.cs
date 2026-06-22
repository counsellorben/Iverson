using System.Diagnostics;

namespace Iverson.Sql;

internal static class Telemetry
{
    internal const string SourceName = "Iverson.Sql";
    internal static readonly ActivitySource Source = new(SourceName, "1.0.0");
}
