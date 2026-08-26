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

    // The drain probe tolerates this many CONSECUTIVE failures before giving up. Any successful
    // poll resets the counter, so a slow drain punctuated by occasional broker blips still
    // completes, while a genuinely broken probe still fails the run rather than reporting drained.
    private const int MaxConsecutiveProbeErrors = 10;
    private static readonly TimeSpan ProbeRetryDelay = TimeSpan.FromSeconds(5);

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

        // Baseline the DLQ before a single document is posted. MessageDispatcher retries 3x and then
        // routes to the DLQ and *returns normally*, so KafkaConsumer commits the offset and a
        // dead-lettered event drives consumer lag to zero exactly like a successful one. Lag alone
        // therefore cannot tell "corpus is searchable" from "some of it was dropped".
        var dlqBaseline = QueryDlqHighWatermark();
        Console.WriteLine($"[benchmark-ingest] DLQ high watermark before ingest: {dlqBaseline:N0}");

        var keyMap = new Dictionary<string, string>();
        long succeeded = 0, failed = 0;

        // BEIR before FreshStack (Step 3): BEIR is ~9K documents and alone answers the fusion
        // question, so ingesting it first hits the spec's largest open risk (A10, laptop ingest
        // feasibility) early, while the stated fallback (BEIR-only) is still available.
        if (beirPresent)
        {
            Console.WriteLine($"[benchmark-ingest] BEIR corpus: {beirCorpus}");
            using var reader = new StreamReader(beirCorpus);
            var corpus = JsonlCorpusParser.ParseCorpus(reader);
            var (s, f) = await IngestAsync(corpus, keyMap, ct);
            succeeded += s; failed += f;
        }

        if (freshStackPresent)
        {
            Console.WriteLine($"[benchmark-ingest] FreshStack corpus: {freshStackCorpus}");
            using var reader = new StreamReader(freshStackCorpus);
            var corpus = JsonlCorpusParser.ParseCorpus(reader);
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

        var dlqAfter = QueryDlqHighWatermark();
        if (dlqAfter > dlqBaseline)
            throw new InvalidOperationException(
                $"[benchmark-ingest] {dlqAfter - dlqBaseline:N0} event(s) were dead-lettered during " +
                $"this ingest (DLQ high watermark {dlqBaseline:N0} -> {dlqAfter:N0}). Those documents " +
                "are not indexed, so the corpus is partial and every Recall number computed from it " +
                "would be depressed by an unknown amount.");

        Console.WriteLine("[benchmark-ingest] Intelligence consumer drained — corpus is searchable.");
    }

    /// <summary>
    /// Sums the high watermark across every partition of <see cref="EntityTopics.Dlq"/>. Compared
    /// before and after the ingest this detects dead-lettered events, which the lag-based drain wait
    /// is structurally blind to (a DLQ'd event is committed like any other).
    /// </summary>
    private long QueryDlqHighWatermark()
    {
        var adminConfig = new AdminClientConfig { BootstrapServers = config.KafkaBootstrap };
        ApplyKafkaSecurity(adminConfig);
        using var admin = new AdminClientBuilder(adminConfig).Build();

        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = config.KafkaBootstrap,
            GroupId          = "iverson.loadtest.dlq-watermark-probe",
            EnableAutoCommit = false,
        };
        ApplyKafkaSecurity(consumerConfig);
        using var consumer = new ConsumerBuilder<Ignore, Ignore>(consumerConfig).Build();

        var metadata = admin.GetMetadata(EntityTopics.Dlq, TimeSpan.FromSeconds(10));
        var topic    = metadata.Topics.SingleOrDefault(t => t.Topic == EntityTopics.Dlq);

        // The DLQ topic not existing yet is the normal state of a fresh stack: nothing has been
        // dead-lettered, so the watermark is zero.
        if (topic is null || topic.Error.Code == ErrorCode.UnknownTopicOrPart)
            return 0;
        if (topic.Error.IsError)
            throw new InvalidOperationException(
                $"Kafka DLQ watermark probe for topic '{EntityTopics.Dlq}' failed: {topic.Error.Reason}");

        long total = 0;
        foreach (var partition in topic.Partitions)
        {
            var wm = consumer.QueryWatermarkOffsets(
                new TopicPartition(EntityTopics.Dlq, new Partition(partition.PartitionId)),
                TimeSpan.FromSeconds(5));
            total += Math.Max(0, wm.High.Value);
        }

        return total;
    }

    private async Task<(long Succeeded, long Failed)> IngestAsync(
        List<CorpusDocument> corpus,
        Dictionary<string, string> keyMap,
        CancellationToken ct)
    {
        long succeeded = 0, failed = 0;
        var done = 0;

        // Bypass only — NOT PickRandom(). BuildAuthorizationRules restricts Body to the
        // iverson-loadtest-bypass role for both read and write, so the regular identity's
        // PersistAsync is rejected outright by RejectDisallowedFields (every payload carries
        // Body). The latency scenarios can afford a random identity; a correctness harness
        // cannot.
        var identity = identities.Bypass;

        // Identity MUST be bound to the coordinator, not passed in a per-call Metadata bag.
        // EntityCoordinator.ResolveHeadersAsync strips any acting-user entry the caller puts in
        // that bag and resolves bound-then-ambient instead — so the pre-parity idiom
        // (`new Metadata().WithActingUser(token)`) sends NO acting-user header at all. The server
        // then treats the call as anonymous: ActingUserInterceptor returns silently on a missing
        // header, and every Post comes back PermissionDenied with `actor=unknown` in the audit
        // log. See Iverson.Client.Core/EntityCoordinator.cs:36-49.
        var writer = documents.WithActingUser(() => identity.GetTokenAsync(ct));

        // The bypass identity's writes are never ownership-checked, so it must set its own
        // OwnerId (same rule WritePathRunner follows for the other benchmark entities).
        var ownerId = await identity.GetSubAsync(ct);

        foreach (var corpusDoc in corpus)
        {
            ct.ThrowIfCancellationRequested();

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
                key = await writer.PersistAsync(doc, ct: ct);
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
        var consecutiveProbeErrors = 0;

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
                    foreach (var tpoe in groupResult.Partitions.Where(p => p.Topic == EntityTopics.Events))
                    {
                        var wm = consumer.QueryWatermarkOffsets(tpoe.TopicPartition, TimeSpan.FromSeconds(5));

                        // Offset.Unset means the group has never committed on this partition — i.e.
                        // nothing on it has been consumed yet. Skipping it (contributing 0 lag) lets
                        // the drain return "drained" after 4 seconds on a fresh stack with nothing
                        // indexed, so count the whole partition as outstanding instead.
                        totalLag += tpoe.Offset == Offset.Unset
                            ? Math.Max(0, wm.High.Value - wm.Low.Value)
                            : Math.Max(0, wm.High.Value - tpoe.Offset.Value);
                    }
                }
            }
            catch (Exception ex)
            {
                // No silent `break`: an errored probe must fail the run, not be mistaken for
                // "drained". But a SINGLE transient error must not discard a post wave that has
                // already fully succeeded. Kafka's coordinator lookup on this stack intermittently
                // times out under embedding load — reproduced directly with kafka-consumer-groups
                // --describe from inside the broker container, which failed once in three attempts
                // while the intelligence consumer was running. Retrying a bounded number of times
                // distinguishes "the probe is broken" from "the broker blinked".
                consecutiveProbeErrors++;
                if (consecutiveProbeErrors > MaxConsecutiveProbeErrors)
                {
                    throw new InvalidOperationException(
                        $"Kafka drain probe for group '{group}' failed {consecutiveProbeErrors} " +
                        $"consecutive times; last error: {ex.Message}", ex);
                }

                Console.WriteLine(
                    $"[benchmark-ingest] Drain probe error {consecutiveProbeErrors}/{MaxConsecutiveProbeErrors} " +
                    $"({ex.GetType().Name}: {ex.Message}) — retrying.");
                await Task.Delay(ProbeRetryDelay, ct);
                continue;
            }

            consecutiveProbeErrors = 0;

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
