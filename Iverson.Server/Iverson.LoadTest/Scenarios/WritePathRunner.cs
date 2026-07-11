using System.Diagnostics;
using System.Text;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Dapper;
using Grpc.Core;
using Iverson.Client.Core;
using Iverson.Events;
using Iverson.LoadTest.Entities;
using Iverson.LoadTest.Reporting;
using Iverson.LoadTest.Seeding;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Iverson.LoadTest.Scenarios;

/// <summary>
/// Shared post-wave / e2e-probe / Kafka-lag-probe logic for both <see cref="WritePathScenario"/>
/// (plaintext Kafka, docker-compose) and <see cref="KindWritePathScenario"/> (TLS+SCRAM Kafka, kind/cloud).
/// The only thing that differs between the two targets is how the Kafka AdminClient/Consumer configs
/// are built, so that's the one seam passed in as <paramref name="applyKafkaSecurity"/>.
/// </summary>
internal static class WritePathRunner
{
    private static readonly string[] Categories =
        ["sports", "tech", "culture", "science", "politics"];

    public static async Task RunAsync(
        LoadTestConfig config,
        EntityCoordinator<BenchmarkArticle> articles,
        EntityCoordinator<BenchmarkAuthor> authors,
        EntityCoordinator<BenchmarkTag> tags,
        ILogger logger,
        CommandFlags flags,
        Action<ClientConfig>? applyKafkaSecurity,
        CancellationToken ct)
    {
        Console.WriteLine($"[write-path] concurrency={flags.Concurrency} count={flags.Count} type={flags.Type}\n");

        // Pre-load author IDs so Article posts have a valid AuthorId
        Guid[] authorIds = [];
        if (flags.Type is "Article" or "article")
        {
            await using var pgForAuthors = new NpgsqlConnection(config.PostgresCs);
            await pgForAuthors.OpenAsync(ct);
            authorIds = await DirectSeeder.LoadAuthorIdsAsync(pgForAuthors);
            if (authorIds.Length == 0)
            {
                Console.Error.WriteLine("No seeded authors found. Run 'dotnet run -- seed' first.");
                return;
            }
            Console.WriteLine($"Loaded {authorIds.Length:N0} author IDs for AuthorId assignment.\n");
        }

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
                        case "Author":
                            var u = new BenchmarkAuthor
                            {
                                Id    = Guid.NewGuid(),
                                Name  = $"WPAuthor {seed}",
                                Email = $"wpauthor{seed}@benchmark.dev",
                                Bio   = new string('x', 200),
                            };
                            await authors.PersistAsync(u, ct);
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
                                Id                = Guid.NewGuid(),
                                Title             = $"WP Article {seed}",
                                Body              = GenerateBody(seed),
                                BenchmarkAuthorId = authorIds.Length > 0
                                    ? authorIds[seed % authorIds.Length]
                                    : Guid.NewGuid(),
                                Category          = Categories[seed % Categories.Length],
                                WordCount         = seed % 1000,
                                PublishedAt       = DateTimeOffset.UtcNow,
                            };
                            await articles.PersistAsync(a, ct);
                            key = a.Id.ToString();
                            break;
                    }

                    report.Record(BenchmarkReport.NowMicros() - t0);
                    postedKeys.Add(key);
                }
                catch (RpcException ex)
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

            var typeName = flags.Type switch
            {
                "Author" => "benchmark_authors",
                "Tag"    => "benchmark_tags",
                _        => "benchmark_articles",
            };

            foreach (var key in sample)
            {
                var t0       = BenchmarkReport.NowMicros();
                var found    = false;
                var deadline = DateTime.UtcNow.AddSeconds(30);

                while (!found && DateTime.UtcNow < deadline)
                {
                    await using var pg = new NpgsqlConnection(config.PostgresCs);
                    await pg.OpenAsync(ct);
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
        await PrintKafkaLagAsync(config, logger, applyKafkaSecurity, file, ct);

        Console.WriteLine($"\nReport saved to {path}");
    }

    private static async Task PrintKafkaLagAsync(
        LoadTestConfig config,
        ILogger logger,
        Action<ClientConfig>? applyKafkaSecurity,
        StreamWriter file,
        CancellationToken ct)
    {
        var adminConfig = new AdminClientConfig { BootstrapServers = config.KafkaBootstrap };
        applyKafkaSecurity?.Invoke(adminConfig);
        using var admin = new AdminClientBuilder(adminConfig).Build();

        // Temporary consumer used only for QueryWatermarkOffsets — no group join
        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = config.KafkaBootstrap,
            GroupId          = "iverson.loadtest.lag-probe",
            EnableAutoCommit = false,
        };
        applyKafkaSecurity?.Invoke(consumerConfig);
        using var consumer = new ConsumerBuilder<Ignore, Ignore>(consumerConfig).Build();

        var groups = new[]
        {
            // No consumer in the codebase uses this group name; retained because no reason to
            // remove it was found during the 2026-07-11 final-review fix-up. Out of scope for
            // that fix — flagged here for a future cleanup pass.
            "iverson.consumer.record",
            "iverson.consumer.engagement",
            "iverson.consumer.intelligence",
        };

        var topics = new[]
        {
            EntityTopics.Events,
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
