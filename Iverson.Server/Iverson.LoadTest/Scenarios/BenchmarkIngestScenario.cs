using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Iverson.Client.Core;
using Iverson.Events;
using Iverson.LoadTest.Auth;
using Iverson.LoadTest.Benchmark;
using Iverson.LoadTest.Corpus;
using Iverson.LoadTest.Entities;
using Microsoft.Extensions.Logging;

namespace Iverson.LoadTest.Scenarios;

/// <summary>
/// Streams a parsed corpus (BEIR and/or FreshStack) through <see cref="EntityCoordinator{T}.PersistAsync"/>
/// and persists the resulting <c>ParentKey -&gt; DocId</c> map to disk (<see cref="KeyMap"/>) — the file
/// Task 4's query scenario reads to translate search results back to corpus doc ids.
///
/// This ingest runs once and its output is shared by all eight sweep configurations (spec §1); nothing
/// in the query path re-ingests. Corpora are read from subdirectories of <c>CommandFlags.CorpusPath</c>:
/// <c>&lt;CorpusPath&gt;/beir/corpus.jsonl</c> and <c>&lt;CorpusPath&gt;/freshstack/corpus.jsonl</c>. BEIR
/// is ingested first when both are present — see Step 3 of the task brief for why (BEIR alone already
/// answers the fusion question, so hitting it first surfaces the laptop-feasibility risk, spec A10, early).
/// </summary>
public sealed class BenchmarkIngestScenario(
    LoadTestConfig                        config,
    KafkaOptions                          kafkaOptions,
    EntityCoordinator<BenchmarkDocument>  documents,
    ActingUserIdentities                  identities,
    ILogger<BenchmarkIngestScenario>      logger)
{
    private const int ProgressEvery = 1_000;

    public async Task RunAsync(CommandFlags flags, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(flags.CorpusPath))
        {
            Console.Error.WriteLine("benchmark-ingest requires --corpus-path.");
            throw new InvalidOperationException("--corpus-path was not provided.");
        }
        if (string.IsNullOrWhiteSpace(flags.KeyMapPath))
        {
            Console.Error.WriteLine("benchmark-ingest requires --key-map-path.");
            throw new InvalidOperationException("--key-map-path was not provided.");
        }

        var beirCorpus       = Path.Combine(flags.CorpusPath, "beir", "corpus.jsonl");
        var freshStackCorpus = Path.Combine(flags.CorpusPath, "freshstack", "corpus.jsonl");
        var beirPresent       = File.Exists(beirCorpus);
        var freshStackPresent = File.Exists(freshStackCorpus);

        if (!beirPresent && !freshStackPresent)
        {
            Console.Error.WriteLine(
                $"No corpus found under '{flags.CorpusPath}' — expected 'beir/corpus.jsonl' and/or 'freshstack/corpus.jsonl'.");
            throw new InvalidOperationException("No corpus found at the given --corpus-path.");
        }

        var keyMap = new Dictionary<string, string>();
        long succeeded = 0, failed = 0;

        // BEIR before FreshStack (Step 3): BEIR is ~9K documents and alone answers the fusion
        // question, so ingesting it first hits the spec's largest open risk (A10, laptop ingest
        // feasibility) early, while the stated fallback (BEIR-only) is still available.
        if (beirPresent)
        {
            Console.WriteLine($"[benchmark-ingest] BEIR corpus: {beirCorpus}");
            using var reader = new StreamReader(beirCorpus);
            var corpus = BeirCorpusParser.ParseCorpus(reader);
            var (s, f) = await IngestAsync(corpus, keyMap, ct);
            succeeded += s; failed += f;
        }

        if (freshStackPresent)
        {
            Console.WriteLine($"[benchmark-ingest] FreshStack corpus: {freshStackCorpus}");
            using var reader = new StreamReader(freshStackCorpus);
            var corpus = FreshStackCorpusParser.ParseCorpus(reader);
            var (s, f) = await IngestAsync(corpus, keyMap, ct);
            succeeded += s; failed += f;
        }

        Console.WriteLine(
            $"[benchmark-ingest] Post wave complete — {succeeded:N0} succeeded, {failed:N0} failed.");

        if (failed > 0)
            throw new InvalidOperationException(
                $"[benchmark-ingest] {failed:N0} document(s) failed to persist — a partial corpus " +
                "silently changes every downstream metric, so the run fails rather than writing a " +
                "key map that Task 4 would treat as complete.");

        await KeyMap.SaveAsync(keyMap, flags.KeyMapPath, ct);
        Console.WriteLine($"[benchmark-ingest] Key map ({keyMap.Count:N0} entries) saved to {flags.KeyMapPath}");

        // Querying before the intelligence consumer drains scores a partial corpus (chunking,
        // embedding and the Qdrant upsert all happen asynchronously after PersistAsync returns) —
        // wait for it here so a "success" from this command means the corpus is actually searchable.
        Console.WriteLine("[benchmark-ingest] Waiting for the intelligence consumer to drain...");
        await DrainIntelligenceConsumerAsync(ct);
        Console.WriteLine("[benchmark-ingest] Intelligence consumer drained — corpus is searchable.");
    }

    private async Task<(long Succeeded, long Failed)> IngestAsync(
        List<CorpusDocument> corpus,
        Dictionary<string, string> keyMap,
        CancellationToken ct)
    {
        long succeeded = 0, failed = 0;
        var done = 0;
        foreach (var corpusDoc in corpus)
        {
            ct.ThrowIfCancellationRequested();

            var identity = identities.PickRandom();
            var headers  = new Grpc.Core.Metadata().WithActingUser(await identity.GetTokenAsync(ct));
            // The server force-sets OwnerId for the owner-restricted identity on create; the
            // bypass identity's writes are never ownership-checked, so it must set its own OwnerId
            // (same rule WritePathRunner follows for the other benchmark entities).
            var ownerId = identity == identities.Bypass ? await identity.GetSubAsync(ct) : "";

            var doc = new BenchmarkDocument
            {
                // Id left unset — the server assigns the UUIDv7 (spec A5).
                DocId   = corpusDoc.DocId,
                Title   = corpusDoc.Title,
                Body    = corpusDoc.Text,
                OwnerId = ownerId,
            };

            string? key;
            try
            {
                key = await documents.PersistAsync(doc, headers, ct);
            }
            catch (Grpc.Core.RpcException ex)
            {
                key = null;
                logger.LogDebug(ex, "Persist failed for DocId={DocId}", corpusDoc.DocId);
            }

            if (key is null)
            {
                failed++;
                logger.LogWarning("Persist returned no key for DocId={DocId}", corpusDoc.DocId);
            }
            else
            {
                keyMap[key] = corpusDoc.DocId;
                succeeded++;
            }

            done++;
            if (done % ProgressEvery == 0)
                Console.WriteLine($"[benchmark-ingest] {done:N0}/{corpus.Count:N0} documents processed...");
        }

        return (succeeded, failed);
    }

    /// <summary>
    /// Waits for consumer group "iverson.consumer.intelligence" to drain on <see cref="EntityTopics.Events"/>,
    /// with no fixed deadline (on ~59K documents through CPU Ollama, draining may take hours — see Step 6).
    /// Unlike <c>WritePathRunner.PrintKafkaLagAsync</c>, this returns success/failure to the caller instead
    /// of silently `break`-ing out, because a silent break here would reintroduce the false-completion
    /// signal this wait exists to prevent.
    /// </summary>
    private async Task DrainIntelligenceConsumerAsync(CancellationToken ct)
    {
        const string group = "iverson.consumer.intelligence";

        var adminConfig = new AdminClientConfig { BootstrapServers = config.KafkaBootstrap };
        ApplyKafkaSecurity(adminConfig);
        using var admin = new AdminClientBuilder(adminConfig).Build();

        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = config.KafkaBootstrap,
            GroupId          = "iverson.loadtest.ingest-drain-probe",
            EnableAutoCommit = false,
        };
        ApplyKafkaSecurity(consumerConfig);
        using var consumer = new ConsumerBuilder<Ignore, Ignore>(consumerConfig).Build();

        var opts = new ListConsumerGroupOffsetsOptions();
        long prevLag = -1;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            long totalLag;

            try
            {
                totalLag = 0;
                var results = await admin.ListConsumerGroupOffsetsAsync(
                    new[] { new ConsumerGroupTopicPartitions(group, null) },
                    opts);

                foreach (var groupResult in results)
                {
                    foreach (var tpoe in groupResult.Partitions.Where(
                        p => p.Topic == EntityTopics.Events && p.Offset != Offset.Unset))
                    {
                        var wm = consumer.QueryWatermarkOffsets(tpoe.TopicPartition, TimeSpan.FromSeconds(5));
                        totalLag += Math.Max(0, wm.High.Value - tpoe.Offset.Value);
                    }
                }
            }
            catch (Exception ex)
            {
                // No silent `break`: an errored probe must fail the run, not be mistaken for "drained".
                throw new InvalidOperationException(
                    $"Kafka drain probe for group '{group}' failed: {ex.Message}", ex);
            }

            Console.WriteLine($"[benchmark-ingest] Intelligence consumer lag: {totalLag:N0} messages ({DateTime.UtcNow:HH:mm:ss})");

            if (totalLag == 0 && prevLag == 0) return;
            prevLag = totalLag;
            await Task.Delay(2_000, ct);
        }
    }

    private void ApplyKafkaSecurity(ClientConfig clientConfig)
    {
        if (!string.IsNullOrWhiteSpace(kafkaOptions.SecurityProtocol))
            KafkaClientConfigFactory.ApplySecurity(clientConfig, kafkaOptions);
    }
}
