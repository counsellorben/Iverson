using Dapper;
using Grpc.Core;
using Iverson.Client.Contracts;
using Iverson.Client.Core;
using Iverson.Events;
using Iverson.LoadTest.Auth;
using Iverson.LoadTest.Entities;
using Iverson.LoadTest.Seeding;
using Iverson.LoadTest.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Npgsql;

var command = args.Length > 0 ? args[0] : "--help";
var flags   = CommandFlags.Parse(args.Length > 1 ? args[1..] : []);

if (flags.Target is not ("containers" or "kind"))
{
    Console.Error.WriteLine($"Invalid --target '{flags.Target}'. Valid values: containers, kind.");
    return 1;
}

var grpcUrl       = Env("IVERSON_GRPC_URL",        "http://localhost:8080");
var clientId      = Environment.GetEnvironmentVariable("IVERSON_CLIENT_ID");
var clientSecret  = Environment.GetEnvironmentVariable("IVERSON_CLIENT_SECRET");
var tokenEndpoint = Environment.GetEnvironmentVariable("IVERSON_TOKEN_ENDPOINT");
var postgresCs   = Env("IVERSON_POSTGRES_CS",     "Host=localhost;Port=5432;Database=iverson;Username=iverson;Password=iverson");
var starRocksCs  = Env("IVERSON_STARROCKS_CS",    "Server=127.0.0.1;Port=9030;Database=iverson;Uid=root;Pwd=;");
var kafkaBoots   = Env("IVERSON_KAFKA_BOOTSTRAP", "localhost:9092");
var actingUserToken = Environment.GetEnvironmentVariable("IVERSON_ACTING_USER_TOKEN");
var actingUserHostHeader = Environment.GetEnvironmentVariable("IVERSON_ACTING_USER_HOST_HEADER") ?? "authentik-server:9000";
// Compose-fixed defaults; kind requires an explicit override (client_id and redirect_uri are
// randomly-generated/ingress-derived per Helm release for kind — see the blueprint templates).
var actingUserClientId    = Environment.GetEnvironmentVariable("IVERSON_ACTING_USER_CLIENT_ID")    ?? "dev-iverson-loadtest-human-client-id";
var actingUserRedirectUri = Environment.GetEnvironmentVariable("IVERSON_ACTING_USER_REDIRECT_URI") ?? "http://localhost/placeholder-callback";
var actingUserUsername = Environment.GetEnvironmentVariable("IVERSON_ACTING_USER_USERNAME") ?? "iverson-acting-user-smoke-test";
var actingUserPassword = Environment.GetEnvironmentVariable("IVERSON_ACTING_USER_PASSWORD") ?? "dev-only-not-for-production-smoke-test-password-0123456789";
var actingUserBypassUsername = Environment.GetEnvironmentVariable("IVERSON_ACTING_USER_BYPASS_USERNAME") ?? "iverson-loadtest-bypass-user";
var actingUserBypassPassword = Environment.GetEnvironmentVariable("IVERSON_ACTING_USER_BYPASS_PASSWORD") ?? "dev-only-not-for-production-bypass-password-0123456789";
var actingUserCacheTarget = flags.Target == "kind" ? "kind" : "compose"; // maps LoadTest's own "containers"/"kind" to the Python script's "compose"/"kind" cache-path vocabulary
var actingUserBaseUrl = tokenEndpoint is not null
    ? tokenEndpoint[..tokenEndpoint.IndexOf("/application/o/token/", StringComparison.Ordinal)]
    : "http://localhost:9000";

var config = new LoadTestConfig(postgresCs, starRocksCs, kafkaBoots);

// Only meaningful for --target kind: the kind/cloud charts' Kafka (Strimzi) exposes just a
// TLS + SCRAM-SHA-512 listener, unlike docker-compose's plaintext broker — see
// charts/kafka/templates/kafka.yaml and KindWritePathScenario. Left at defaults (no security)
// when running against containers, where these env vars are simply never set.
var kafkaOptions = new KafkaOptions
{
    BootstrapServers = kafkaBoots,
    SecurityProtocol = Environment.GetEnvironmentVariable("IVERSON_KAFKA_SECURITY_PROTOCOL"),
    SaslMechanism    = Environment.GetEnvironmentVariable("IVERSON_KAFKA_SASL_MECHANISM"),
    SaslUsername     = Environment.GetEnvironmentVariable("IVERSON_KAFKA_SASL_USERNAME"),
    SaslPassword     = Environment.GetEnvironmentVariable("IVERSON_KAFKA_SASL_PASSWORD"),
    SslCaLocation    = Environment.GetEnvironmentVariable("IVERSON_KAFKA_SSL_CA_LOCATION"),
};

if (flags.Target == "kind" && string.IsNullOrWhiteSpace(kafkaOptions.SecurityProtocol))
{
    Console.Error.WriteLine(
        "--target kind requires Kafka TLS/SASL settings (IVERSON_KAFKA_SECURITY_PROTOCOL, " +
        "IVERSON_KAFKA_SASL_MECHANISM, IVERSON_KAFKA_SASL_USERNAME, IVERSON_KAFKA_SASL_PASSWORD, " +
        "IVERSON_KAFKA_SSL_CA_LOCATION) — the kind/cloud charts' Kafka has no plaintext listener.");
    return 1;
}

var clientCredentials = clientId is not null && clientSecret is not null && tokenEndpoint is not null
    ? new IversonClientCredentials(clientId, clientSecret, tokenEndpoint)
    : null;

var services = new ServiceCollection()
    .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning))
    .AddIversonClient(grpcUrl, clientCredentials, entityAssemblies: [typeof(BenchmarkArticle).Assembly])
    .AddSingleton(config)
    .AddSingleton(kafkaOptions)
    .AddSingleton(sp => new ActingUserIdentities(
        new ActingUserTokenProvider(new AuthentikFlowExecutorClient(
            new AuthentikIdentityConfig(
                actingUserUsername, actingUserPassword, actingUserClientId, actingUserRedirectUri,
                actingUserBaseUrl, actingUserHostHeader, actingUserCacheTarget),
            sp.GetRequiredService<ILogger<AuthentikFlowExecutorClient>>())),
        new ActingUserTokenProvider(new AuthentikFlowExecutorClient(
            new AuthentikIdentityConfig(
                actingUserBypassUsername, actingUserBypassPassword, actingUserClientId, actingUserRedirectUri,
                actingUserBaseUrl, actingUserHostHeader, actingUserCacheTarget),
            sp.GetRequiredService<ILogger<AuthentikFlowExecutorClient>>()))))
    .AddSingleton<DirectSeeder>()
    .AddSingleton<WritePathScenario>()
    .AddSingleton<KindWritePathScenario>()
    .AddSingleton<ReadPathScenario>()
    .BuildServiceProvider();

if (command is "seed" or "write-path" or "read-path" or "all")
{
    Console.WriteLine("Registering schemas...");
    try
    {
        var authorizationByTypeName = new Dictionary<string, AuthorizationRules>
        {
            ["BenchmarkArticle"] = BuildAuthorizationRules("Body"),
            ["BenchmarkAuthor"]  = BuildAuthorizationRules("Email"),
            ["BenchmarkTag"]     = BuildAuthorizationRules("Category"),
        };
        var tenantFieldByTypeName = new Dictionary<string, string>
        {
            ["BenchmarkArticle"] = "TenantId",
            ["BenchmarkAuthor"]  = "TenantId",
            ["BenchmarkTag"]     = "TenantId",
        };
        await services.GetRequiredService<SchemaRegistrar>().RegisterAllAsync(authorizationByTypeName, tenantFieldByTypeName);
        Console.WriteLine("Schemas registered.\n");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Schema registration failed: {ex.Message}");
        Console.Error.WriteLine("Is the Iverson API running? (dotnet run in Iverson.Api, or docker compose up iverson-api)");
        return 1;
    }
}

Task RunWritePathAsync() => flags.Target == "kind"
    ? services.GetRequiredService<KindWritePathScenario>().RunAsync(flags)
    : services.GetRequiredService<WritePathScenario>().RunAsync(flags);

switch (command)
{
    case "seed":
        await services.GetRequiredService<DirectSeeder>().RunAsync(flags);
        break;
    case "write-path":
        await RunWritePathAsync();
        break;
    case "read-path":
        await services.GetRequiredService<ReadPathScenario>().RunAsync(flags);
        break;
    case "all":
        await services.GetRequiredService<DirectSeeder>().RunAsync(flags);
        await RunWritePathAsync();
        await services.GetRequiredService<ReadPathScenario>().RunAsync(flags);
        break;
    case "clear-data":
        await ClearDataAsync(config.StarRocksCs, config.PostgresCs);
        break;
    case "acting-user-smoke-test":
        if (actingUserToken is null)
        {
            Console.Error.WriteLine("acting-user-smoke-test requires IVERSON_ACTING_USER_TOKEN.");
            return 1;
        }
        var searchClient = services.GetRequiredService<ObjectSearchService.ObjectSearchServiceClient>();
        var headers = new Metadata().WithActingUser(actingUserToken);
        try
        {
            await searchClient.AggregateAsync(new AggregateRequest(), headers);
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Unauthenticated)
        {
            // Expected: an empty AggregateRequest fails business-logic validation
            // (RequireSchema/FailedPrecondition). The auth layer already accepted the
            // call by the time this throws — that's what this command is checking.
        }
        Console.WriteLine("acting-user-smoke-test: call passed the auth layer — check API logs for the structured log line.");
        break;
    default:
        Console.WriteLine("""
            Usage: dotnet run -- <command> [options]

            Commands:
              seed           Seed benchmark data directly into Postgres and StarRocks
              write-path     Benchmark gRPC Post → Kafka → consumer pipeline
              read-path      Benchmark GetMany / Search / Aggregate via gRPC
              all            Run seed → write-path → read-path in sequence
              clear-data     Drop all StarRocks tables and truncate Postgres benchmark tables for greenfield re-registration
              acting-user-smoke-test  Exercise the acting-user auth layer with an Aggregate call carrying
                                      IVERSON_ACTING_USER_TOKEN via the x-acting-user-authorization metadata header

            Options:
              --force-reseed         Truncate and re-seed even if data already present
              --concurrency <N>      Parallel tasks (default: 16)
              --count <N>            Records to post in write-path (default: 10000)
              --iterations <N>       Iterations per sub-scenario in read-path (default: 1000)
              --type <name>          Entity type for write-path: Article|Author|Tag (default: Article)
              --target <name>        Environment write-path's Kafka targets: containers|kind (default: containers)
                                     "containers" = plaintext Kafka (docker-compose). "kind" = TLS+SCRAM
                                     Kafka (kind/cloud Helm charts) — requires IVERSON_KAFKA_SECURITY_PROTOCOL,
                                     IVERSON_KAFKA_SASL_MECHANISM, IVERSON_KAFKA_SASL_USERNAME,
                                     IVERSON_KAFKA_SASL_PASSWORD, IVERSON_KAFKA_SSL_CA_LOCATION.
            """);
        break;
}

return 0;

static string Env(string key, string def) =>
    Environment.GetEnvironmentVariable(key) ?? def;

static AuthorizationRules BuildAuthorizationRules(string restrictedField) => new()
{
    OwnerField = "OwnerId",
    RowPermissions =
    {
        new RowPermission { Role = "iverson-loadtest-bypass", CanReadAll = true, CanWriteAll = true, CanDeleteAll = true },
    },
    FieldPermissions =
    {
        new FieldPermission
        {
            FieldName = restrictedField,
            ReadableRoles = { "iverson-loadtest-bypass" },
            WritableRoles = { "iverson-loadtest-bypass" },
        },
    },
};

static async Task ClearDataAsync(string starRocksCs, string postgresCs)
{
    await using var sr = new MySqlConnection(starRocksCs);
    await sr.OpenAsync();

    var tables = (await sr.QueryAsync<string>("SHOW TABLES")).ToList();

    if (tables.Count == 0)
        Console.WriteLine("StarRocks: no tables found — nothing to drop.");
    else
    {
        foreach (var table in tables)
        {
            await sr.ExecuteAsync($"DROP TABLE IF EXISTS `{table}`");
            Console.WriteLine($"StarRocks dropped: {table}");
        }
        Console.WriteLine($"StarRocks: dropped {tables.Count} table(s).\n");
    }

    await using var pg = new NpgsqlConnection(postgresCs);
    await pg.OpenAsync();

    await pg.ExecuteAsync(
        "TRUNCATE benchmark_articles, benchmark_authors, benchmark_tags RESTART IDENTITY CASCADE");
    Console.WriteLine("Postgres: truncated benchmark_articles, benchmark_authors, benchmark_tags.");

    await pg.ExecuteAsync(
        "DO $$ BEGIN IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'benchmark_users') THEN TRUNCATE benchmark_users CASCADE; END IF; END $$");
    Console.WriteLine("Postgres: cleared benchmark_users (legacy).");
}

// ── Supporting types ──────────────────────────────────────────────────────────

public sealed record LoadTestConfig(string PostgresCs, string StarRocksCs, string KafkaBootstrap);

public sealed class CommandFlags
{
    public bool   ForceReseed { get; init; }
    public int    Concurrency { get; init; } = 16;
    public int    Count       { get; init; } = 10_000;
    public int    Iterations  { get; init; } = 1_000;
    public string Type        { get; init; } = "Article";
    public string Target      { get; init; } = "containers";

    public static CommandFlags Parse(string[] args) => new()
    {
        ForceReseed = args.Contains("--force-reseed"),
        Concurrency = IntFlag(args, "--concurrency", 16),
        Count       = IntFlag(args, "--count",       10_000),
        Iterations  = IntFlag(args, "--iterations",  1_000),
        Type        = StrFlag(args, "--type",        "Article"),
        Target      = StrFlag(args, "--target",      "containers"),
    };

    private static int    IntFlag(
        string[] a,
        string f,
        int    d)
    {
        var i = Array.IndexOf(a, f);
        return i >= 0 && i + 1 < a.Length && int.TryParse(a[i + 1], out var v)
            ? v
            : d;
    }

    private static string StrFlag(
        string[] a,
        string f,
        string d)
    {
        var i = Array.IndexOf(a, f);
        return i >= 0 && i + 1 < a.Length
            ? a[i + 1]
            : d;
    }
}
