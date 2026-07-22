using System.Diagnostics;
using System.Text;
using Confluent.Kafka;
using Dapper;
using Grpc.Core;
using Iverson.Client.Core;
using Iverson.Events;
using Iverson.LoadTest.Auth;
using Iverson.LoadTest.Entities;
using Iverson.LoadTest.Scenarios;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Npgsql;
using NpgsqlTypes;

namespace Iverson.LoadTest.Seeding;

public sealed class DirectSeeder(
    LoadTestConfig config,
    ActingUserIdentities identities,
    EntityCoordinator<BenchmarkArticle> articleCoordinator,
    EntityCoordinator<BenchmarkAuthor> authorCoordinator,
    EntityCoordinator<BenchmarkTag> tagCoordinator,
    KafkaOptions kafkaOptions,
    ILogger<DirectSeeder> logger)
{
    private const int ArticleTarget = 400_000;
    private const int AuthorTarget  =  50_000;
    private const int TagTarget     =  10_000;

    private static readonly string[] Categories =
        ["sports", "tech", "culture", "science", "politics"];

    public async Task RunAsync(CommandFlags flags, CancellationToken ct = default)
    {
        await using var pg = new NpgsqlConnection(config.PostgresCs);
        await pg.OpenAsync(ct);
        await using var sr = new MySqlConnection(config.StarRocksCs);
        await sr.OpenAsync(ct);

        var authorIds = await SeedAuthorsAsync(pg, sr, flags, flags.ForceReseed, ct);
        await SeedTagsAsync(pg, sr, flags, flags.ForceReseed, ct);
        await SeedArticlesAsync(pg, sr, authorIds, flags, flags.ForceReseed, ct);

        Console.WriteLine("\nWaiting for StarRocks projections to catch up...");
        Action<Confluent.Kafka.ClientConfig>? applyKafkaSecurity = flags.Target == "kind"
            ? c => KafkaClientConfigFactory.ApplySecurity(c, kafkaOptions)
            : null;
        await WritePathRunner.PrintKafkaLagAsync(config, logger, applyKafkaSecurity, file: null, ct);

        Console.WriteLine("\nSeeding complete.");
    }

    internal static async Task<Guid[]> LoadAuthorIdsAsync(NpgsqlConnection pg) =>
        [.. (await pg.QueryAsync<Guid>("SELECT \"Id\" FROM benchmark_authors"))];

    // ── Authors ───────────────────────────────────────────────────────────────

    private async Task<Guid[]> SeedAuthorsAsync(
        NpgsqlConnection pg, MySqlConnection sr, CommandFlags flags, bool force, CancellationToken ct)
    {
        var existing = await pg.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM benchmark_authors");

        if (!force && existing >= AuthorTarget * 0.95)
        {
            Console.WriteLine($"[Authors] {existing:N0} rows already present. Skipping.");
            return await LoadAuthorIdsAsync(pg);
        }

        var ownerSub = await identities.Regular.GetSubAsync(ct);

        if (force)
        {
            await pg.ExecuteAsync("TRUNCATE TABLE benchmark_authors CASCADE");
            await sr.ExecuteAsync("DELETE FROM `benchmark_authors` WHERE 1=1");
        }

        var ids = new Guid[AuthorTarget];
        var sw  = Stopwatch.StartNew();

        // ── Postgres COPY ──
        await using var writer = await pg.BeginBinaryImportAsync(
            "COPY benchmark_authors (\"Id\", \"Name\", \"Email\", \"Bio\", \"OwnerId\", \"TenantId\") FROM STDIN (FORMAT BINARY)",
            ct);

        for (var i = 0; i < AuthorTarget; i++)
        {
            ids[i] = Guid.NewGuid();
            var ownerId  = i % 100 == 0 ? ownerSub : Guid.NewGuid().ToString();
            var tenantId = i % 2 == 0 ? "tenant-smoke-test" : "tenant-bypass";
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(ids[i],                      NpgsqlDbType.Uuid, ct);
            await writer.WriteAsync($"Author {i}",               NpgsqlDbType.Text, ct);
            await writer.WriteAsync($"author{i}@benchmark.dev",  NpgsqlDbType.Text, ct);
            await writer.WriteAsync(new string('x', 200),        NpgsqlDbType.Text, ct);
            await writer.WriteAsync(ownerId,                     NpgsqlDbType.Text, ct);
            await writer.WriteAsync(tenantId,                    NpgsqlDbType.Text, ct);
            if (i % 5_000 == 0) PrintProgress("Authors", i, AuthorTarget, sw);
        }
        await writer.CompleteAsync(ct);

        // ── StarRocks via gRPC (server lazily provisions the per-tenant database) ──
        // Email is the field BuildAuthorizationRules restricts to the bypass role (Program.cs).
        // Setting it conditionally doesn't avoid the rejection — the server's field-permission
        // check rejects based on whether the "Email" key is present in the payload at all, not
        // its value, and every property (including an empty string) is always serialized. So
        // identities.Regular's posts here are expected to be rejected with InvalidArgument;
        // PostToStarRocksAsync's try/catch absorbs and logs them, and only identities.Bypass's
        // half of AuthorTarget actually lands in StarRocks.
        await PostToStarRocksAsync(AuthorTarget, flags.Concurrency, logger, async i =>
        {
            var identity = i % 2 == 0 ? identities.Regular : identities.Bypass;
            var entity = new BenchmarkAuthor
            {
                Id      = Guid.NewGuid(),
                Name    = $"Author {i}",
                Email   = $"author{i}@benchmark.dev",
                Bio     = new string('x', 200),
                OwnerId = identity == identities.Bypass ? await identity.GetSubAsync(ct) : "",
            };
            var headers = new Grpc.Core.Metadata().WithActingUser(await identity.GetTokenAsync(ct));
            await authorCoordinator.PersistAsync(entity, headers, ct);
        }, ct);

        Console.WriteLine($"\n[Authors] Seeded {AuthorTarget:N0} rows — {sw.Elapsed.TotalSeconds:F1}s");
        return ids;
    }

    // ── Tags ──────────────────────────────────────────────────────────────────

    private async Task SeedTagsAsync(
        NpgsqlConnection pg, MySqlConnection sr, CommandFlags flags, bool force, CancellationToken ct)
    {
        var existing = await pg.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM benchmark_tags");

        if (!force && existing >= TagTarget * 0.95)
        {
            Console.WriteLine($"[Tags] {existing:N0} rows already present. Skipping.");
            return;
        }

        var ownerSub = await identities.Regular.GetSubAsync(ct);

        if (force)
        {
            await pg.ExecuteAsync("TRUNCATE TABLE benchmark_tags CASCADE");
            await sr.ExecuteAsync("DELETE FROM `benchmark_tags` WHERE 1=1");
        }

        var ids = new Guid[TagTarget];
        var sw  = Stopwatch.StartNew();

        await using var writer = await pg.BeginBinaryImportAsync(
            "COPY benchmark_tags (\"Id\", \"Name\", \"Category\", \"OwnerId\", \"TenantId\") FROM STDIN (FORMAT BINARY)",
            ct);

        for (var i = 0; i < TagTarget; i++)
        {
            ids[i] = Guid.NewGuid();
            var ownerId  = i % 100 == 0 ? ownerSub : Guid.NewGuid().ToString();
            var tenantId = i % 2 == 0 ? "tenant-smoke-test" : "tenant-bypass";
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(ids[i],                       NpgsqlDbType.Uuid, ct);
            await writer.WriteAsync($"tag-{i}",                   NpgsqlDbType.Text, ct);
            await writer.WriteAsync(Categories[i % Categories.Length], NpgsqlDbType.Text, ct);
            await writer.WriteAsync(ownerId,                      NpgsqlDbType.Text, ct);
            await writer.WriteAsync(tenantId,                     NpgsqlDbType.Text, ct);
        }
        await writer.CompleteAsync(ct);

        await PostToStarRocksAsync(TagTarget, flags.Concurrency, logger, async i =>
        {
            var identity = i % 2 == 0 ? identities.Regular : identities.Bypass;
            var entity = new BenchmarkTag
            {
                Id       = Guid.NewGuid(),
                Name     = $"tag-{i}",
                Category = Categories[i % Categories.Length],
                OwnerId  = identity == identities.Bypass ? await identity.GetSubAsync(ct) : "",
            };
            var headers = new Grpc.Core.Metadata().WithActingUser(await identity.GetTokenAsync(ct));
            await tagCoordinator.PersistAsync(entity, headers, ct);
        }, ct);

        Console.WriteLine($"[Tags] Seeded {TagTarget:N0} rows — {sw.Elapsed.TotalSeconds:F1}s");
    }

    // ── Articles ──────────────────────────────────────────────────────────────

    private async Task SeedArticlesAsync(
        NpgsqlConnection pg, MySqlConnection sr, Guid[] authorIds, CommandFlags flags, bool force, CancellationToken ct)
    {
        var existing = await pg.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM benchmark_articles");

        if (!force && existing >= ArticleTarget * 0.95)
        {
            Console.WriteLine($"[Articles] {existing:N0} rows already present. Skipping.");
            return;
        }

        var ownerSub = await identities.Regular.GetSubAsync(ct);

        if (force)
        {
            await pg.ExecuteAsync("TRUNCATE TABLE benchmark_articles CASCADE");
            await sr.ExecuteAsync("DELETE FROM `benchmark_articles` WHERE 1=1");
        }

        var ids      = new Guid[ArticleTarget];
        var sw       = Stopwatch.StartNew();
        var baseDate = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        await using var writer = await pg.BeginBinaryImportAsync(
            "COPY benchmark_articles " +
            "(\"Id\", \"Title\", \"Body\", \"BenchmarkAuthorId\", \"Category\", \"WordCount\", \"PublishedAt\", \"OwnerId\", \"TenantId\") " +
            "FROM STDIN (FORMAT BINARY)",
            ct);

        for (var i = 0; i < ArticleTarget; i++)
        {
            ids[i] = Guid.NewGuid();
            var cat  = Categories[i % Categories.Length];
            var body = GenerateBody(i);
            var ownerId  = i % 100 == 0 ? ownerSub : Guid.NewGuid().ToString();
            var tenantId = i % 2 == 0 ? "tenant-smoke-test" : "tenant-bypass";

            await writer.StartRowAsync(ct);
            await writer.WriteAsync(ids[i],                           NpgsqlDbType.Uuid,        ct);
            await writer.WriteAsync($"Benchmark Article {i}: {cat}",  NpgsqlDbType.Text,        ct);
            await writer.WriteAsync(body,                             NpgsqlDbType.Text,        ct);
            await writer.WriteAsync(authorIds[i % authorIds.Length],  NpgsqlDbType.Uuid,        ct);
            await writer.WriteAsync(cat,                              NpgsqlDbType.Text,        ct);
            await writer.WriteAsync(body.Length / 5,                  NpgsqlDbType.Integer,     ct);
            await writer.WriteAsync(baseDate.AddDays(i % 2190),       NpgsqlDbType.TimestampTz, ct);
            await writer.WriteAsync(ownerId,                          NpgsqlDbType.Text,        ct);
            await writer.WriteAsync(tenantId,                         NpgsqlDbType.Text,        ct);

            if (i % 10_000 == 0) PrintProgress("Articles", i, ArticleTarget, sw);
        }
        await writer.CompleteAsync(ct);

        Console.WriteLine($"\n[Articles/Postgres] done — {sw.Elapsed.TotalSeconds:F1}s");

        await PostToStarRocksAsync(ArticleTarget, flags.Concurrency, logger, async i =>
        {
            var identity = i % 2 == 0 ? identities.Regular : identities.Bypass;
            var cat = Categories[i % Categories.Length];
            var body = GenerateBody(i);
            var entity = new BenchmarkArticle
            {
                Id                = Guid.NewGuid(),
                Title             = $"Benchmark Article {i}: {cat}",
                Body              = body,
                BenchmarkAuthorId = authorIds[i % authorIds.Length],
                Category          = cat,
                WordCount         = body.Length / 5,
                PublishedAt       = baseDate.AddDays(i % 2190),
                OwnerId           = identity == identities.Bypass ? await identity.GetSubAsync(ct) : "",
            };
            var headers = new Grpc.Core.Metadata().WithActingUser(await identity.GetTokenAsync(ct));
            await articleCoordinator.PersistAsync(entity, headers, ct);
        }, ct);

        Console.WriteLine($"[Articles] Seeded {ArticleTarget:N0} rows — total {sw.Elapsed.TotalSeconds:F1}s");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string GenerateBody(int seed)
    {
        var sb = new StringBuilder(4200);
        for (var i = 0; i < 80; i++)
            sb.Append($"Article {seed} paragraph {i}: The quick brown fox jumps over the lazy dog near record {seed}. ");
        return sb.ToString()[..4096];
    }

    private static void PrintProgress(string label, int done, int total, Stopwatch sw)
    {
        var pct     = done * 100.0 / total;
        var elapsed = sw.Elapsed.TotalSeconds;
        var rps     = elapsed > 0 ? done / elapsed : 0;
        var eta     = rps > 0 ? (total - done) / rps : 0;
        Console.Write($"\r[{label}] {done:N0} / {total:N0} ({pct:F1}%) — {rps:F0} rec/sec — ETA {eta:F0}s   ");
    }

    private static async Task PostToStarRocksAsync(
        int count, int concurrency, ILogger logger, Func<int, Task> postOneAsync, CancellationToken ct)
    {
        var perTask = count / concurrency;
        var tasks = Enumerable.Range(0, concurrency).Select(taskIdx => Task.Run(async () =>
        {
            for (var i = 0; i < perTask; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await postOneAsync(taskIdx * perTask + i);
                }
                catch (RpcException ex)
                {
                    logger.LogDebug(ex, "StarRocks post failed at index {Index}", taskIdx * perTask + i);
                }
            }
        }, ct));
        await Task.WhenAll(tasks);
    }
}
