using System.Diagnostics;
using System.Text;
using Dapper;
using Iverson.LoadTest.Auth;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Npgsql;
using NpgsqlTypes;

namespace Iverson.LoadTest.Seeding;

public sealed class DirectSeeder(LoadTestConfig config, ActingUserIdentities identities)
{
    private const int ArticleTarget = 400_000;
    private const int AuthorTarget  =  50_000;
    private const int TagTarget     =  10_000;
    private const int SrBatchSize   =   1_000;

    private static readonly string[] Categories =
        ["sports", "tech", "culture", "science", "politics"];

    public async Task RunAsync(CommandFlags flags, CancellationToken ct = default)
    {
        await using var pg = new NpgsqlConnection(config.PostgresCs);
        await pg.OpenAsync(ct);
        await using var sr = new MySqlConnection(config.StarRocksCs);
        await sr.OpenAsync(ct);

        var authorIds = await SeedAuthorsAsync(pg, sr, flags.ForceReseed, ct);
        await SeedTagsAsync(pg, sr, flags.ForceReseed, ct);
        await SeedArticlesAsync(pg, sr, authorIds, flags.ForceReseed, ct);

        Console.WriteLine("\nSeeding complete.");
    }

    internal static async Task<Guid[]> LoadAuthorIdsAsync(NpgsqlConnection pg) =>
        [.. (await pg.QueryAsync<Guid>("SELECT \"Id\" FROM benchmark_authors"))];

    // ── Authors ───────────────────────────────────────────────────────────────

    private async Task<Guid[]> SeedAuthorsAsync(
        NpgsqlConnection pg, MySqlConnection sr, bool force, CancellationToken ct)
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
            "COPY benchmark_authors (\"Id\", \"Name\", \"Email\", \"Bio\", \"OwnerId\") FROM STDIN (FORMAT BINARY)",
            ct);

        for (var i = 0; i < AuthorTarget; i++)
        {
            ids[i] = Guid.NewGuid();
            var ownerId = i % 100 == 0 ? ownerSub : Guid.NewGuid().ToString();
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(ids[i],                      NpgsqlDbType.Uuid, ct);
            await writer.WriteAsync($"Author {i}",               NpgsqlDbType.Text, ct);
            await writer.WriteAsync($"author{i}@benchmark.dev",  NpgsqlDbType.Text, ct);
            await writer.WriteAsync(new string('x', 200),        NpgsqlDbType.Text, ct);
            await writer.WriteAsync(ownerId,                     NpgsqlDbType.Text, ct);
            if (i % 5_000 == 0) PrintProgress("Authors", i, AuthorTarget, sw);
        }
        await writer.CompleteAsync(ct);

        // ── StarRocks batch INSERT ──
        await SrBatchInsertAsync(sr, "benchmark_authors",
            ["Id", "Name", "Email", "Bio", "OwnerId"],
            ids.Select((id, i) => new object[]
            {
                id.ToString(), $"Author {i}", $"author{i}@benchmark.dev", new string('x', 200),
                i % 100 == 0 ? ownerSub : Guid.NewGuid().ToString()
            }),
            sw, ct);

        Console.WriteLine($"\n[Authors] Seeded {AuthorTarget:N0} rows — {sw.Elapsed.TotalSeconds:F1}s");
        return ids;
    }

    // ── Tags ──────────────────────────────────────────────────────────────────

    private async Task SeedTagsAsync(
        NpgsqlConnection pg, MySqlConnection sr, bool force, CancellationToken ct)
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
            "COPY benchmark_tags (\"Id\", \"Name\", \"Category\", \"OwnerId\") FROM STDIN (FORMAT BINARY)",
            ct);

        for (var i = 0; i < TagTarget; i++)
        {
            ids[i] = Guid.NewGuid();
            var ownerId = i % 100 == 0 ? ownerSub : Guid.NewGuid().ToString();
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(ids[i],                       NpgsqlDbType.Uuid, ct);
            await writer.WriteAsync($"tag-{i}",                   NpgsqlDbType.Text, ct);
            await writer.WriteAsync(Categories[i % Categories.Length], NpgsqlDbType.Text, ct);
            await writer.WriteAsync(ownerId,                      NpgsqlDbType.Text, ct);
        }
        await writer.CompleteAsync(ct);

        await SrBatchInsertAsync(sr, "benchmark_tags",
            ["Id", "Name", "Category", "OwnerId"],
            ids.Select((id, i) => new object[]
            {
                id.ToString(), $"tag-{i}", Categories[i % Categories.Length],
                i % 100 == 0 ? ownerSub : Guid.NewGuid().ToString()
            }),
            sw, ct);

        Console.WriteLine($"[Tags] Seeded {TagTarget:N0} rows — {sw.Elapsed.TotalSeconds:F1}s");
    }

    // ── Articles ──────────────────────────────────────────────────────────────

    private async Task SeedArticlesAsync(
        NpgsqlConnection pg, MySqlConnection sr, Guid[] authorIds, bool force, CancellationToken ct)
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
            "(\"Id\", \"Title\", \"Body\", \"BenchmarkAuthorId\", \"Category\", \"WordCount\", \"PublishedAt\", \"OwnerId\") " +
            "FROM STDIN (FORMAT BINARY)",
            ct);

        for (var i = 0; i < ArticleTarget; i++)
        {
            ids[i] = Guid.NewGuid();
            var cat  = Categories[i % Categories.Length];
            var body = GenerateBody(i);
            var ownerId = i % 100 == 0 ? ownerSub : Guid.NewGuid().ToString();

            await writer.StartRowAsync(ct);
            await writer.WriteAsync(ids[i],                           NpgsqlDbType.Uuid,        ct);
            await writer.WriteAsync($"Benchmark Article {i}: {cat}",  NpgsqlDbType.Text,        ct);
            await writer.WriteAsync(body,                             NpgsqlDbType.Text,        ct);
            await writer.WriteAsync(authorIds[i % authorIds.Length],  NpgsqlDbType.Uuid,        ct);
            await writer.WriteAsync(cat,                              NpgsqlDbType.Text,        ct);
            await writer.WriteAsync(body.Length / 5,                  NpgsqlDbType.Integer,     ct);
            await writer.WriteAsync(baseDate.AddDays(i % 2190),       NpgsqlDbType.TimestampTz, ct);
            await writer.WriteAsync(ownerId,                          NpgsqlDbType.Text,        ct);

            if (i % 10_000 == 0) PrintProgress("Articles", i, ArticleTarget, sw);
        }
        await writer.CompleteAsync(ct);

        Console.WriteLine($"\n[Articles/Postgres] done — {sw.Elapsed.TotalSeconds:F1}s");

        var srSw = Stopwatch.StartNew();
        await SrBatchInsertAsync(sr, "benchmark_articles",
            ["Id", "Title", "BenchmarkAuthorId", "Category", "WordCount", "PublishedAt", "OwnerId"],
            ids.Select((id, i) =>
            {
                var cat = Categories[i % Categories.Length];
                return new object[]
                {
                    id.ToString(),
                    $"Benchmark Article {i}: {cat}",
                    authorIds[i % authorIds.Length].ToString(),
                    cat,
                    GenerateBody(i).Length / 5,
                    baseDate.AddDays(i % 2190).ToString("yyyy-MM-dd HH:mm:ss"),
                    i % 100 == 0 ? ownerSub : Guid.NewGuid().ToString(),
                };
            }),
            srSw, ct);

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

    private static async Task SrBatchInsertAsync(
        MySqlConnection conn, string table, string[] cols,
        IEnumerable<object[]> rows, Stopwatch sw, CancellationToken ct)
    {
        var colList = string.Join(", ", cols.Select(c => $"`{c}`"));
        var batch   = new List<object[]>(SrBatchSize);
        var total   = 0;

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            batch.Add(row);
            if (batch.Count >= SrBatchSize)
            {
                await FlushBatchAsync(conn, table, colList, batch);
                total += batch.Count;
                Console.Write($"\r[{table}/SR] {total:N0} rows — {sw.Elapsed.TotalSeconds:F1}s   ");
                batch.Clear();
            }
        }
        if (batch.Count > 0)
        {
            await FlushBatchAsync(conn, table, colList, batch);
            total += batch.Count;
        }
    }

    private static async Task FlushBatchAsync(
        MySqlConnection conn, string table, string colList, List<object[]> batch)
    {
        var sb  = new StringBuilder($"INSERT INTO `{table}` ({colList}) VALUES ");
        var cmd = new MySqlCommand { Connection = conn };

        for (var i = 0; i < batch.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('(');
            for (var j = 0; j < batch[i].Length; j++)
            {
                if (j > 0) sb.Append(',');
                var p = $"@p{i}_{j}";
                sb.Append(p);
                cmd.Parameters.AddWithValue(p, batch[i][j] ?? DBNull.Value);
            }
            sb.Append(')');
        }

        cmd.CommandText = sb.ToString();
        await cmd.ExecuteNonQueryAsync();
    }
}
