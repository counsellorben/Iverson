using System.Diagnostics;

namespace Iverson.StarRocks;

internal static class Telemetry
{
    internal static readonly ActivitySource Source = new("Iverson.StarRocks");
}
