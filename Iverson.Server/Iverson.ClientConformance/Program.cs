using Iverson.ClientConformance;
using Microsoft.Extensions.Logging;

var flags = CliFlags.Parse(args);

if (flags.ShowHelp)
{
    PrintUsage();
    return 0;
}

var grpcUrl = Env("IVERSON_GRPC_URL", "http://localhost:8080");
var postgresCs = Env("IVERSON_POSTGRES_CS", "Host=localhost;Port=5432;Database=iverson;Username=iverson;Password=iverson");
// Single source of truth for the Authentik base URL — TokenBroker derives it the same way, so
// Preflight and TokenBroker never diverge on which host they check/hit.
var authentikBaseUrl = TokenBroker.DeriveAuthentikBaseUrl(Environment.GetEnvironmentVariable("IVERSON_TOKEN_ENDPOINT"));

Console.WriteLine("Running preflight checks...");
var preflight = new Preflight(grpcUrl, authentikBaseUrl, postgresCs);
var failures = await preflight.RunAsync();
if (failures.Count > 0)
{
    foreach (var failure in failures)
        Console.Error.WriteLine($"  FAIL: {failure}");
    return 1;
}
Console.WriteLine("Preflight checks passed.\n");

using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
using var tokenBroker = new TokenBroker(grpcUrl, loggerFactory);

try
{
    Console.WriteLine($"Ensuring tenant '{tokenBroker.TenantId}' is provisioned...");
    await tokenBroker.EnsureTenantProvisionedAsync();
    Console.WriteLine($"Tenant '{tokenBroker.TenantId}' ready.\n");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Tenant provisioning failed: {ex.Message}");
    return 1;
}

// Driver invocation and scenario execution are wired in later tasks; this task's Program.cs only
// proves the CLI, preflight, token broker and report skeleton hold together end to end.
var report = new Report();

Console.WriteLine(report.RenderText());

if (flags.JsonPath is not null)
    report.WriteJson(flags.JsonPath);

return report.AllPassed ? 0 : 1;

static string Env(string key, string def) =>
    Environment.GetEnvironmentVariable(key) ?? def;

static void PrintUsage() => Console.WriteLine("""
    Usage: dotnet run -- [options]

    Options:
      --languages <csv>   Restrict the run to these languages (e.g. dotnet,python). Default: all.
      --scenarios <csv>   Restrict the run to these scenarios. Default: all.
      --json <path>       Also write the report as JSON to this path.
      --keep              Keep driver-created data instead of tearing it down.
      --help              Show this message.
    """);

namespace Iverson.ClientConformance
{
    /// <summary>
    /// The orchestrator's CLI surface. <see cref="Languages"/> and <see cref="Scenarios"/> are
    /// null when unrestricted (run everything) — later tasks intersect these against the actual
    /// driver/scenario registries.
    /// </summary>
    public sealed record CliFlags(
        IReadOnlyList<string>? Languages,
        IReadOnlyList<string>? Scenarios,
        string? JsonPath,
        bool Keep,
        bool ShowHelp)
    {
        public static CliFlags Parse(string[] args) => new(
            Languages: CsvFlag(args, "--languages"),
            Scenarios: CsvFlag(args, "--scenarios"),
            JsonPath: StrFlag(args, "--json"),
            Keep: args.Contains("--keep"),
            ShowHelp: args.Contains("--help") || args.Contains("-h"));

        private static IReadOnlyList<string>? CsvFlag(string[] a, string f)
        {
            var value = StrFlag(a, f);
            return value is null
                ? null
                : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        private static string? StrFlag(string[] a, string f)
        {
            var i = Array.IndexOf(a, f);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
        }
    }
}
