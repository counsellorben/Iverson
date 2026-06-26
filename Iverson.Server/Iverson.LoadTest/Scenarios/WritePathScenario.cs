using System.Diagnostics;
using System.Text;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Dapper;
using Iverson.Client.Core;
using Iverson.LoadTest.Entities;
using Iverson.LoadTest.Reporting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Iverson.LoadTest.Scenarios;

public sealed class WritePathScenario(
    LoadTestConfig config,
    EntityCoordinator<BenchmarkArticle> articles,
    EntityCoordinator<BenchmarkUser>    users,
    EntityCoordinator<BenchmarkTag>     tags,
    ILogger<WritePathScenario>          logger)
{
    private static readonly string[] Categories =
        ["sports", "tech", "culture", "science", "politics"];

    public async Task RunAsync(CommandFlags flags, CancellationToken ct = default)
    {
        Console.WriteLine($"[write-path] concurrency={flags.Concurrency} count={flags.Count} type={flags.Type}\n");

        var report     = new BenchmarkReport();
        var postedKeys = new System.Collections.Concurrent.ConcurrentBag<string>();

        // ── Post wave ──────────────────────────────────────────────────────────
        var sw      = Stopwatch.StartNew();
        var perTask = flags.Count / flags.Concurrency;

        var tasks = Enumerable.Range(0, flags.Concurrency).Select(taskIdx => Task.Run(async () =>
        {
            for (var i = 0; i < perTask; i++)
            {
                ct.ThrowIfCancellationRequested();
                var seed = taskIdx * perTask + i;

                try
                {
                    var t0 = BenchmarkReport.NowMicros();
                    string key;

                    switch (flags.Type)
                    {
                        case "User":
                            var u = new BenchmarkUser
                            {
                                Id    = Guid.NewGuid(),
                                Name  = $"WPUser {seed}",
                                Email = $"wp{seed}@benchmark.dev",
                                Bio   = new string('x', 200),
                            };
                            await users.PersistAsync(u, ct);
                            key = u.Id.ToString();
                            break;

                        case "Tag":
                            var tg = new BenchmarkTag
                            {
                                Id       = Guid.NewGuid(),
                                Name     = $"wptag-{seed}",
                                Category = Categories[seed % Categories.Length],
                            };
                            await tags.PersistAsync(tg, ct);
                            key = tg.Id.ToString();
                            break;

                        default: // Article
                            var a = new BenchmarkArticle
                            {
                                Id          = Guid.NewGuid(),
                                Title       = $"WP Article {seed}",
                                Body        = GenerateBody(seed),
                                Category    = Categories[seed % Categories.Length],
                                WordCount   = seed % 1000,
                                PublishedAt = DateTimeOffset.UtcNow,
                            };
                            await articles.PersistAsync(a, ct);
                            key = a.Id.ToString();
                            break;
                    }

                    report.Record(BenchmarkReport.NowMicros() - t0);
                    postedKeys.Add(key);
                }
                catch (Exception ex)
                {
                    report.RecordError();
                    logger.LogDebug(ex, "Post failed at seed={Seed}", seed);
                }
            }
        }, ct)).ToArray();

        await Task.WhenAll(tasks);
        sw.Stop();

        Console.WriteLine($"[write-path] Post wave complete — {flags.Count:N0} records in {sw.Elapsed.TotalSeconds:F1}s");

        // ── Print gRPC Post report ─────────────────────────────────────────────
        var path = BenchmarkReport.ResultsPath($"write-path-{flags.Type.ToLower()}");
        await using var file = new StreamWriter(path, append: false);
        report.Print($"gRPC Post | type={flags.Type} | concurrency={flags.Concurrency} | count={flags.Count}", file);

        // ── End-to-end latency probe (sample 100 keys) ─────────────────────────
        var sample = postedKeys.Take(100).ToArray();
        if (sample.Length > 0)
        {
            Console.WriteLine($"\n[write-path] Probing end-to-end visibility for {sample.Length} keys...");
            var e2eReport = new BenchmarkReport();
            await using var pg = new NpgsqlConnection(config.PostgresCs);
            await pg.OpenAsync(ct);

            var typeName = flags.Type switch
            {
                "User" => "benchmark_users",
                "Tag"  => "benchmark_tags",
                _      => "benchmark_articles",
            };

            foreach (var key in sample)
            {
                var t0       = BenchmarkReport.NowMicros();
                var found    = false;
                var deadline = DateTime.UtcNow.AddSeconds(30);

                while (!found && DateTime.UtcNow < deadline)
                {
                    var count = await pg.ExecuteScalarAsync<int>(
                        $"SELECT COUNT(*) FROM {typeName} WHERE \"Id\" = @id::uuid",
                        new { id = key });
                    if (count > 0) found = true;
                    else await Task.Delay(500, ct);
                }

                if (found) e2eReport.Record(BenchmarkReport.NowMicros() - t0);
                else e2eReport.RecordError();
            }

            e2eReport.Print("End-to-end (Post → Postgres visible)", file);
        }

        // ── Kafka consumer lag probe ───────────────────────────────────────────
        Console.WriteLine("[write-path] Sampling Kafka consumer lag...");
        await PrintKafkaLagAsync(file, ct);

        Console.WriteLine($"\nReport saved to {path}");
    }

    private async Task PrintKafkaLagAsync(StreamWriter file, CancellationToken ct)
    {
        using var admin = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = config.KafkaBootstrap }).Build();

        // Temporary consumer used only for QueryWatermarkOffsets — no group join
        using var consumer = new ConsumerBuilder<Ignore, Ignore>(new ConsumerConfig
        {
            BootstrapServers = config.KafkaBootstrap,
            GroupId          = "iverson.loadtest.lag-probe",
            EnableAutoCommit = false,
        }).Build();

        var groups = new[]
        {
            "iverson.consumer.record",
            "iverson.consumer.engagement",
            "iverson.consumer.intelligence",
        };

        var topics = new[]
        {
            "iverson.entity.created",
            "iverson.entity.updated",
        };

        var opts = new ListConsumerGroupOffsetsOptions();

        // Poll every 2s for up to 60s, stopping when lag reaches 0
        var deadline = DateTime.UtcNow.AddSeconds(60);
        long prevLag  = -1;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            long totalLag = 0;

            try
            {
                // API requires exactly 1 group per call — iterate groups individually
                foreach (var group in groups)
                {
                    var results = await admin.ListConsumerGroupOffsetsAsync(
                        new[] { new ConsumerGroupTopicPartitions(group, null) },
                        opts);

                    foreach (var groupResult in results)
                    {
                        foreach (var tpoe in groupResult.Partitions.Where(
                            p => topics.Contains(p.Topic) && p.Offset != Offset.Unset))
                        {
                            var wm = consumer.QueryWatermarkOffsets(tpoe.TopicPartition, TimeSpan.FromSeconds(5));
                            totalLag += Math.Max(0, wm.High.Value - tpoe.Offset.Value);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Kafka lag probe failed");
                break;
            }

            var line = $"  Kafka lag: {totalLag:N0} messages ({DateTime.UtcNow:HH:mm:ss})";
            Console.WriteLine(line);
            file.WriteLine(line);

            if (totalLag == 0 && prevLag == 0) break;
            prevLag = totalLag;
            await Task.Delay(2_000, ct);
        }
    }

    private static string GenerateBody(int seed)
    {
        var sb = new StringBuilder(4200);
        for (var i = 0; i < 80; i++)
            sb.Append($"Article {seed} paragraph {i}: The quick brown fox jumps over the lazy dog near record {seed}. ");
        return sb.ToString()[..4096];
    }
}
