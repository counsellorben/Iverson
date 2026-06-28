using Dapper;
using Iverson.Client.Core;
using Iverson.LoadTest.Entities;
using Iverson.LoadTest.Seeding;
using Iverson.LoadTest.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Npgsql;

var command = args.Length > 0 ? args[0] : "--help";
var flags   = CommandFlags.Parse(args.Length > 1 ? args[1..] : []);

var grpcUrl      = Env("IVERSON_GRPC_URL",        "http://localhost:8080");
var postgresCs   = Env("IVERSON_POSTGRES_CS",     "Host=localhost;Port=5432;Database=iverson;Username=iverson;Password=iverson");
var starRocksCs  = Env("IVERSON_STARROCKS_CS",    "Server=127.0.0.1;Port=9030;Database=iverson;Uid=root;Pwd=;");
var kafkaBoots   = Env("IVERSON_KAFKA_BOOTSTRAP", "localhost:9092");

var config   = new LoadTestConfig(postgresCs, starRocksCs, kafkaBoots);

var services = new ServiceCollection()
    .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning))
    .AddIversonClient(grpcUrl, typeof(BenchmarkArticle).Assembly)
    .AddSingleton(config)
    .AddSingleton<DirectSeeder>()
    .AddSingleton<WritePathScenario>()
    .AddSingleton<ReadPathScenario>()
    .BuildServiceProvider();

if (command is "seed" or "write-path" or "read-path" or "all")
{
    Console.WriteLine("Registering schemas...");
    try
    {
        await services.GetRequiredService<SchemaRegistrar>().RegisterAllAsync();
        Console.WriteLine("Schemas registered.\n");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Schema registration failed: {ex.Message}");
        Console.Error.WriteLine("Is the Iverson API running? (dotnet run in Iverson.Api, or docker compose up iverson-api)");
        return 1;
    }
}

switch (command)
{
    case "seed":
        await services.GetRequiredService<DirectSeeder>().RunAsync(flags);
        break;
    case "write-path":
        await services.GetRequiredService<WritePathScenario>().RunAsync(flags);
        break;
    case "read-path":
        await services.GetRequiredService<ReadPathScenario>().RunAsync(flags);
        break;
    case "all":
        await services.GetRequiredService<DirectSeeder>().RunAsync(flags);
        await services.GetRequiredService<WritePathScenario>().RunAsync(flags);
        await services.GetRequiredService<ReadPathScenario>().RunAsync(flags);
        break;
    case "reset-starrocks":
        await ResetStarRocksAsync(config.StarRocksCs, config.PostgresCs);
        break;
    default:
        Console.WriteLine("""
            Usage: dotnet run -- <command> [options]

            Commands:
              seed           Seed benchmark data directly into Postgres and StarRocks
              write-path     Benchmark gRPC Post → Kafka → consumer pipeline
              read-path      Benchmark GetMany / Search / Aggregate via gRPC
              all            Run seed → write-path → read-path in sequence
              reset-starrocks  Drop all StarRocks tables and truncate Postgres benchmark tables for greenfield re-registration

            Options:
              --force-reseed         Truncate and re-seed even if data already present
              --concurrency <N>      Parallel tasks (default: 16)
              --count <N>            Records to post in write-path (default: 10000)
              --iterations <N>       Iterations per sub-scenario in read-path (default: 1000)
              --type <name>          Entity type for write-path: Article|User|Tag (default: Article)
            """);
        break;
}

return 0;

static string Env(string key, string def) =>
    Environment.GetEnvironmentVariable(key) ?? def;

static async Task ResetStarRocksAsync(string starRocksCs, string postgresCs)
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

    public static CommandFlags Parse(string[] args) => new()
    {
        ForceReseed = args.Contains("--force-reseed"),
        Concurrency = IntFlag(args, "--concurrency", 16),
        Count       = IntFlag(args, "--count",       10_000),
        Iterations  = IntFlag(args, "--iterations",  1_000),
        Type        = StrFlag(args, "--type",        "Article"),
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
