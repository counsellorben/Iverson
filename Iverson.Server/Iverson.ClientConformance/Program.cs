using Grpc.Core;
using Grpc.Net.Client;
using Iverson.Client.Contracts;
using Iverson.ClientConformance;
using Iverson.ClientConformance.Scenarios;
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

// The harness runs entirely inside the acting user's own tenant and never creates one. That
// tenant needs no provisioning call to be checked: the server validates it on EVERY call
// carrying an acting-user token (ActingUserInterceptor rejects an absent, suspended or deleted
// tenant with PermissionDenied), so an unusable tenant surfaces immediately and with a better
// message than a probe here could produce. What DOES have to be resolved up front is which
// tenant that is — the drivers must stamp the acting user's own tenant_id on the rows they
// write, or the server refuses to read a single one of them back.
string actingTenant;
try
{
    actingTenant = await tokenBroker.GetActingTenantAsync();
    Console.WriteLine($"Acting user's tenant: '{actingTenant}'.\n");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Could not resolve the acting user's tenant: {ex.Message}");
    return 1;
}

var report = new Report();

try
{
    // The five driver rows the matrix has, in report order.
    string[] allLanguages = ["dotnet", "python", "typescript", "go", "java"];
    var languages = flags.Languages ?? allLanguages;

    // Single source of truth: the recognized scenario names double as the default (unrestricted)
    // run set, so a newly added scenario can never join the recognized list without also joining
    // the default — the drift that let `--scenarios` omitted silently mean "crud-roundtrip only".
    string[] recognizedScenarios =
    [
        CrudRoundtripScenario.Name, NamingRejectedScenario.Name, NavPropertyRejectedScenario.Name,
        InteropScenario.Name, SchemaCatalogScenario.Name, QueryScenario.Name,
        VectorSearchScenario.Name, IdentityScenario.Name, ErrorContractScenario.Name,
    ];
    var scenarios = flags.Scenarios ?? recognizedScenarios;

    var actingToken = await tokenBroker.GetActingTokenAsync();

    // S8 identity is the only scenario that needs a SECOND acting-user identity, and minting one
    // runs a full interactive Authentik flow — so it is minted only when that scenario is actually
    // in the run set, rather than on every `--scenarios query` invocation. An empty pair here is
    // not silently tolerated by the scenario: IdentityScenario.PreconditionFailure refuses to grade
    // a negative leg that has no wrong identity to send.
    var runsIdentity = scenarios.Contains(IdentityScenario.Name, StringComparer.OrdinalIgnoreCase);
    var wrongActingToken = runsIdentity ? await tokenBroker.GetOtherTenantActingTokenAsync() : string.Empty;
    var otherTenant = runsIdentity ? await tokenBroker.GetOtherTenantAsync() : string.Empty;
    if (runsIdentity)
        Console.WriteLine($"Wrong acting user's tenant: '{otherTenant}'.\n");
    var ownerId = await tokenBroker.GetOwnerIdAsync();
    var serviceToken = await tokenBroker.GetServiceTokenAsync();

    // Both identities on one channel, exactly as the drivers wire them: the service token as
    // channel call credentials (`authorization`), the acting-user token per call
    // (`x-acting-user-authorization`). UnsafeUseInsecureChannelCallCredentials is required or
    // the credentials are silently dropped over plaintext h2c — no exception, no header.
    var callCredentials = CallCredentials.FromInterceptor((_, metadata) =>
    {
        metadata.Add("Authorization", $"Bearer {serviceToken}");
        return Task.CompletedTask;
    });
    using var channel = GrpcChannel.ForAddress(grpcUrl, new GrpcChannelOptions
    {
        UnsafeUseInsecureChannelCallCredentials = true,
        Credentials = ChannelCredentials.Create(ChannelCredentials.Insecure, callCredentials),
    });

    var mapping = new ObjectMappingService.ObjectMappingServiceClient(channel);
    var runner = new DriverRunner();
    var crudRoundtrip = new CrudRoundtripScenario(
        runner, mapping, new Reregistrar(mapping), new PostgresProbe(postgresCs), Console.WriteLine);
    var namingRejected = new NamingRejectedScenario(runner, mapping);
    var navPropertyRejected = new NavPropertyRejectedScenario(mapping);
    var interop = new InteropScenario(runner, new Reregistrar(mapping), Console.WriteLine);
    var schemaCatalog = new SchemaCatalogScenario(runner, new Reregistrar(mapping), Console.WriteLine);
    var query = new QueryScenario(
        runner, new Reregistrar(mapping),
        new ObjectSearchService.ObjectSearchServiceClient(channel),
        log: Console.WriteLine);
    var identity = new IdentityScenario(runner, new Reregistrar(mapping), log: Console.WriteLine);
    var errorContract = new ErrorContractScenario(runner, new Reregistrar(mapping), log: Console.WriteLine);
    var vectorSearch = new VectorSearchScenario(
        runner, new Reregistrar(mapping),
        new ObjectSearchService.ObjectSearchServiceClient(channel),
        log: Console.WriteLine);

    DriverContext BuildContext(string scenarioName) => new(
        Scenario: scenarioName,
        // Empty on purpose: `--type` is only a per-driver hint for which captured descriptor
        // is the root type, and every driver falls back to its own root when it is empty.
        // One shared context cannot name five different language-specific type names.
        Type: string.Empty,
        Tenant: actingTenant,
        GrpcUrl: grpcUrl,
        ClientId: Environment.GetEnvironmentVariable("IVERSON_CLIENT_ID"),
        ClientSecret: Environment.GetEnvironmentVariable("IVERSON_CLIENT_SECRET"),
        TokenEndpoint: Environment.GetEnvironmentVariable("IVERSON_TOKEN_ENDPOINT"),
        ActingToken: actingToken,
        OwnerId: ownerId,
        IdPrefix: $"c{DateTime.UtcNow:yyyyMMddHHmmss}",
        ServiceToken: serviceToken,
        WrongActingToken: wrongActingToken);

    if (scenarios.Contains(CrudRoundtripScenario.Name, StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine($"Running scenario '{CrudRoundtripScenario.Name}'...");
        foreach (var cell in await crudRoundtrip.RunAsync(languages, BuildContext(CrudRoundtripScenario.Name), actingToken))
            report.Add(cell);
    }

    if (scenarios.Contains(NamingRejectedScenario.Name, StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine($"Running scenario '{NamingRejectedScenario.Name}'...");
        foreach (var cell in await namingRejected.RunAsync(languages, BuildContext(NamingRejectedScenario.Name), actingToken))
            report.Add(cell);
    }

    if (scenarios.Contains(NavPropertyRejectedScenario.Name, StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine($"Running scenario '{NavPropertyRejectedScenario.Name}'...");
        foreach (var cell in await navPropertyRejected.RunAsync(languages, BuildContext(NavPropertyRejectedScenario.Name), actingToken))
            report.Add(cell);
    }

    if (scenarios.Contains(InteropScenario.Name, StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine($"Running scenario '{InteropScenario.Name}'...");
        foreach (var cell in await interop.RunAsync(languages, BuildContext(InteropScenario.Name), actingToken))
            report.Add(cell);
    }

    if (scenarios.Contains(SchemaCatalogScenario.Name, StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine($"Running scenario '{SchemaCatalogScenario.Name}'...");
        foreach (var cell in await schemaCatalog.RunAsync(languages, BuildContext(SchemaCatalogScenario.Name), actingToken))
            report.Add(cell);
    }

    // The dispatch, not `recognizedScenarios`, is what actually runs a scenario: line 71 reads
    // `flags.Scenarios ?? recognizedScenarios`, so `--scenarios query` bypasses the recognized
    // list entirely. The registration above governs the DEFAULT (unfiltered) run set and the
    // unknown-name warning below; this block governs whether the scenario runs at all.
    if (scenarios.Contains(QueryScenario.Name, StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine($"Running scenario '{QueryScenario.Name}'...");
        foreach (var cell in await query.RunAsync(languages, BuildContext(QueryScenario.Name), actingToken))
            report.Add(cell);
    }

    // As with `query` above, this dispatch block — not `recognizedScenarios` — is what actually
    // runs the scenario.
    if (scenarios.Contains(VectorSearchScenario.Name, StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine($"Running scenario '{VectorSearchScenario.Name}'...");
        foreach (var cell in await vectorSearch.RunAsync(languages, BuildContext(VectorSearchScenario.Name), actingToken))
            report.Add(cell);
    }

    // As with `query` and `vector-search` above, this dispatch block — not `recognizedScenarios` —
    // is what actually runs the scenario.
    if (scenarios.Contains(IdentityScenario.Name, StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine($"Running scenario '{IdentityScenario.Name}'...");
        foreach (var cell in await identity.RunAsync(
                     languages, BuildContext(IdentityScenario.Name), actingToken, otherTenant))
        {
            report.Add(cell);
        }
    }

    // As with `query`, `vector-search` and `identity` above, this dispatch block — not
    // `recognizedScenarios` — is what actually runs the scenario.
    if (scenarios.Contains(ErrorContractScenario.Name, StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine($"Running scenario '{ErrorContractScenario.Name}'...");
        foreach (var cell in await errorContract.RunAsync(
                     languages, BuildContext(ErrorContractScenario.Name), actingToken))
        {
            report.Add(cell);
        }
    }

    foreach (var unknown in scenarios.Where(s => !recognizedScenarios.Contains(s, StringComparer.OrdinalIgnoreCase)))
        Console.Error.WriteLine($"  unknown scenario '{unknown}' — ignored");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Harness run failed: {ex}");
    return 1;
}

Console.WriteLine();
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
