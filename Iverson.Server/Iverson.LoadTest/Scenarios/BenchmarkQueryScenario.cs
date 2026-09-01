using System.Text.Json;
using System.Text.Json.Nodes;
using Grpc.Core;
using Iverson.Client.Contracts;
using Iverson.Client.Core;
using Iverson.Client.Search;
using Iverson.LoadTest.Auth;
using Iverson.LoadTest.Benchmark;
using Iverson.LoadTest.Corpus;
using Iverson.LoadTest.Entities;
using Microsoft.Extensions.Logging;

namespace Iverson.LoadTest.Scenarios;

/// <summary>
/// Runs both vector RPCs (SearchSimilar, SearchChunks) against a fully-drained
/// <see cref="BenchmarkDocument"/> corpus, aggregates chunk results to document-level scores via
/// <see cref="MaxPassageAggregator"/>, and writes one TREC run file per RPC. This scenario computes
/// no metrics itself (alpha-nDCG/nDCG/Recall are external, spec §1) — it only writes rows.
///
/// Constructor-injects the raw <see cref="ObjectSearchService.ObjectSearchServiceClient"/> rather than
/// <c>EntityCoordinator</c>: SearchSimilar/SearchChunks take no <see cref="Metadata"/> parameter through
/// the coordinator's convenience wrappers, so acting-user headers have to be attached by hand on the raw
/// client, exactly as <see cref="BenchmarkIngestScenario"/> does for PersistAsync (spec A21 — an
/// unauthenticated query is denied into an empty stream, not an error).
///
/// This assumes Task 3's ingest has fully drained (its Step 6 wait) — querying a partially indexed
/// corpus produces non-empty run files with silently wrong numbers.
/// </summary>
public sealed class BenchmarkQueryScenario(
    ObjectSearchService.ObjectSearchServiceClient search,
    ActingUserIdentities                          identities,
    LoadTestConfig                                config,
    ILogger<BenchmarkQueryScenario>               logger)
{
    // SearchSimilar's top_k counts entities (50 results = 50 documents). SearchChunks' top_k counts
    // chunks and the server does not dedup by parent (spec A22), so a 50-chunk request would collapse
    // to well under 50 distinct documents after max-passage aggregation and understate Recall@50.
    // This multiplier is fixed for the whole sweep (Global Constraint) — never varied per configuration.
    private const int    DocumentBudget        = 50;
    private const int    ChunkBudgetMultiplier = 5;
    private const string SimilarRunSuffix      = "similar";
    private const string ChunksRunSuffix       = "chunks";

    // ingest.py writes documents/chunks in lowercase (plus embed_calls/embeds_saved/elapsed_seconds,
    // which this scenario has no use for and JsonSerializer silently ignores).
    private static readonly JsonSerializerOptions StatsJsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonSerializerOptions SidecarWriteOptions =
        new() { WriteIndented = true };

    private sealed record IngestStats(int Documents, int Chunks);

    public async Task RunAsync(CommandFlags flags, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(flags.CorpusPath))
        {
            Console.Error.WriteLine("benchmark-query requires --corpus-path.");
            throw new InvalidOperationException("--corpus-path was not provided.");
        }
        if (string.IsNullOrWhiteSpace(flags.KeyMapPath))
        {
            Console.Error.WriteLine("benchmark-query requires --key-map-path.");
            throw new InvalidOperationException("--key-map-path was not provided.");
        }
        if (string.IsNullOrWhiteSpace(flags.OutputDir))
        {
            Console.Error.WriteLine("benchmark-query requires --output-dir.");
            throw new InvalidOperationException("--output-dir was not provided.");
        }
        if (string.IsNullOrWhiteSpace(flags.ConfigLabel))
        {
            Console.Error.WriteLine("benchmark-query requires --config-label.");
            throw new InvalidOperationException("--config-label was not provided.");
        }

        // Refuse a chunk budget that cannot reach DocumentBudget distinct documents (see
        // ChunkBudgetGuard's doc comment). ingest.py writes this sidecar; the C# ingest path does
        // not, so its absence is not itself an error -- refusing here would block corpora ingested
        // that way.
        var statsPath = $"{flags.KeyMapPath}.stats.json";
        if (!File.Exists(statsPath))
        {
            Console.WriteLine(
                $"[benchmark-query] No stats sidecar at {statsPath} -- skipping the chunk-budget guard.");
        }
        else
        {
            var stats = JsonSerializer.Deserialize<IngestStats>(
                            await File.ReadAllTextAsync(statsPath, ct), StatsJsonOptions)
                        ?? throw new InvalidOperationException($"{statsPath} could not be parsed.");

            var r = ChunkBudgetGuard.Evaluate(stats.Documents, stats.Chunks, DocumentBudget, ChunkBudgetMultiplier);
            if (!r.Ok)
            {
                // MinimumMultiplier is Ceiling(ChunksPerDocument), computed unconditionally -- when
                // ChunksPerDocument is not finite or not positive (a degenerate .stats.json, e.g. a
                // missing "documents"/"chunks" count) that ceiling is nonsense (e.g. int.MinValue),
                // so the "raise to" advice is suppressed rather than printed.
                var advice = double.IsFinite(r.ChunksPerDocument) && r.ChunksPerDocument > 0
                    ? $"Raise ChunkBudgetMultiplier to >= {r.MinimumMultiplier}."
                    : "The stats sidecar's document/chunk counts are unusable -- fix or regenerate it " +
                      "rather than raising ChunkBudgetMultiplier.";
                Console.Error.WriteLine(
                    $"""
                    REFUSING: chunk budget cannot reach DocumentBudget distinct documents.

                      corpus              {r.ChunksPerDocument:F2} chunks/doc  ({stats.Chunks:N0} chunks / {stats.Documents:N0} documents)
                      DocumentBudget      {DocumentBudget}
                      ChunkBudgetMult     {ChunkBudgetMultiplier}
                      chunk top_k         {r.ChunkTopK}
                      reachable documents ~{r.ReachableDocuments:F0}  (worst case: a document's chunks retrieved together)

                      {advice}
                    """);
                throw new InvalidOperationException("chunk budget cannot reach DocumentBudget distinct documents.");
            }
        }

        // Record which build produced this run. A run that cannot be attributed to a build should
        // not start -- and an API that cannot answer a read-only GET will not serve the sweep either.
        using (var http = new HttpClient())
        {
            HttpResponseMessage buildResponse;
            var buildUrl = $"{config.HttpUrl}/build";
            try
            {
                buildResponse = await http.GetAsync(buildUrl, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine(
                    $"REFUSING: could not reach {buildUrl} to attribute this run: {ex.Message}");
                throw new InvalidOperationException("could not fetch /build for run attribution.", ex);
            }

            if (!buildResponse.IsSuccessStatusCode)
            {
                Console.Error.WriteLine(
                    $"REFUSING: {buildUrl} returned {(int)buildResponse.StatusCode} " +
                    $"{buildResponse.StatusCode} -- a run that cannot be attributed to a build must not start.");
                throw new InvalidOperationException("/build did not return success for run attribution.");
            }

            var buildBody = await buildResponse.Content.ReadAsStreamAsync(ct);
            var buildJson = await JsonNode.ParseAsync(buildBody, cancellationToken: ct)
                            ?? throw new InvalidOperationException($"{buildUrl} returned an empty body.");

            if (buildJson["composite"] is null)
            {
                Console.Error.WriteLine(
                    $"REFUSING: {buildUrl} returned no \"composite\" -- a run that cannot be " +
                    "attributed to a build must not start.");
                throw new InvalidOperationException("/build response had no composite for run attribution.");
            }

            var sidecar = new JsonObject
            {
                ["configLabel"]   = flags.ConfigLabel,
                ["composite"]     = buildJson["composite"]?.DeepClone(),
                ["assemblies"]    = buildJson["assemblies"]?.DeepClone(),
                ["recordedAtUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            };

            Directory.CreateDirectory(flags.OutputDir);
            var sidecarPath = Path.Combine(flags.OutputDir, $"{flags.ConfigLabel}.meta.json");
            await File.WriteAllTextAsync(
                sidecarPath, sidecar.ToJsonString(SidecarWriteOptions), ct);
            Console.WriteLine($"[benchmark-query] Wrote {sidecarPath}");
        }

        var keyMap = await KeyMap.LoadAsync(flags.KeyMapPath, ct);
        Console.WriteLine($"[benchmark-query] Loaded key map ({keyMap.Count:N0} entries) from {flags.KeyMapPath}");

        var queries = LoadQueries(flags.CorpusPath);
        Console.WriteLine($"[benchmark-query] Loaded {queries.Count:N0} queries.");

        if (queries.Count == 0)
        {
            Console.Error.WriteLine(
                $"No queries found under '{flags.CorpusPath}' — expected 'beir/queries.jsonl' " +
                "and/or 'freshstack/queries.jsonl'.");
            throw new InvalidOperationException("No queries found at the given --corpus-path.");
        }

        var similarResults = new List<(string QueryId, IReadOnlyList<(string DocId, double Score)> Ranked)>();
        var chunksResults  = new List<(string QueryId, IReadOnlyList<(string DocId, double Score)> Ranked)>();

        var done = 0;
        long failures = 0;
        var unresolvedParents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var query in queries)
        {
            ct.ThrowIfCancellationRequested();

            // Bypass only — NOT PickRandom(). Body is readable by the iverson-loadtest-bypass role
            // alone, so the regular identity is rejected on the searched property and would
            // contribute an empty, silently-missing result set for that query.
            var identity = identities.Bypass;
            var headers  = new Metadata().WithActingUser(await identity.GetTokenAsync(ct));

            var similar = await RunSimilarAsync(query, headers, ct);
            var chunks  = await RunChunksAsync(query, headers, keyMap, ct);
            failures += similar.Failed + chunks.Failed;
            foreach (var parentKey in chunks.Unresolved)
                unresolvedParents.Add(parentKey);

            similarResults.Add((query.QueryId, similar.Ranked));
            chunksResults.Add((query.QueryId, chunks.Ranked));

            done++;
            if (done % 25 == 0)
                Console.WriteLine($"[benchmark-query] {done:N0}/{queries.Count:N0} queries processed...");
        }

        var similarPath = Path.Combine(flags.OutputDir, $"{flags.ConfigLabel}.{SimilarRunSuffix}.trec");
        var chunksPath  = Path.Combine(flags.OutputDir, $"{flags.ConfigLabel}.{ChunksRunSuffix}.trec");

        await TrecRunWriter.WriteAsync(similarPath, similarResults, flags.ConfigLabel, ct);
        await TrecRunWriter.WriteAsync(chunksPath, chunksResults, flags.ConfigLabel, ct);

        Console.WriteLine($"[benchmark-query] Wrote {similarPath}");
        Console.WriteLine($"[benchmark-query] Wrote {chunksPath}");

        // A failed RPC contributes zero rows for that query, which the external scorer reads as a
        // score of 0 — indistinguishable from a genuine miss. The run files are written first (they
        // are useful for diagnosis) and then the command fails, so no silently-partial run file is
        // ever mistaken for a complete one.
        if (failures > 0)
            throw new InvalidOperationException(
                $"[benchmark-query] {failures:N0} search RPC(s) failed — the run files above are " +
                "incomplete and must not be scored.");

        // Same contract as the failures check above: the run files are written first because they are
        // useful for diagnosis, and the command then fails so they cannot be mistaken for scoreable
        // output. An unresolved parent is a document that IS in the index but is NOT in this run's key
        // map — every chunk of it was dropped, so the chunk ranking is missing candidates it should
        // have ranked and its Recall is understated by an unknown amount.
        if (unresolvedParents.Count > 0)
            throw new InvalidOperationException(
                $"[benchmark-query] {unresolvedParents.Count:N0} parent key(s) returned by SearchChunks " +
                $"are absent from the key map at {flags.KeyMapPath} — the index holds documents this run " +
                "cannot name, so the run files above are built from an unknown corpus and must not be " +
                $"scored. First few: {string.Join(", ", unresolvedParents.Take(5))}. Either drop the " +
                "tenant's Qdrant collections and re-ingest (clear-data does NOT touch Qdrant), or pass a " +
                "key map covering every ingest the collection holds.");
    }

    private async Task<(IReadOnlyList<(string DocId, double Score)> Ranked, int Failed)> RunSimilarAsync(
        CorpusQuery query, Metadata headers, CancellationToken ct)
    {
        var request = Query.Similar<BenchmarkDocument>(d => d.Body)
            .Text(query.Text)
            .TopK(DocumentBudget)
            .Build();

        var results = new List<(string DocId, double Score)>();
        var failed  = 0;
        try
        {
            using var call = search.SearchSimilar(request, headers, cancellationToken: ct);
            await foreach (var r in call.ResponseStream.ReadAllAsync(ct))
            {
                // Data is a Struct keyed by descriptor column name — PascalCase, matching the
                // entity property — with values typed from each column's SQL type; it is not a
                // deserialized entity, and StructConverter is internal to Core (P10). DocId is a
                // TEXT column, so its value stays a string. BuildObjectPointPayload omits a scalar
                // whose value is null, so "DocId" can still be absent — indexing would throw
                // KeyNotFoundException past the catch below and kill the whole sweep over one bad
                // result.
                if (!r.Data.Fields.TryGetValue("DocId", out var docIdValue))
                {
                    logger.LogWarning(
                        "SearchSimilar result for QueryId={QueryId} has no DocId payload field; skipping it.",
                        query.QueryId);
                    continue;
                }

                results.Add((docIdValue.StringValue, r.Score));
            }
        }
        catch (RpcException ex)
        {
            logger.LogWarning(ex, "SearchSimilar failed for QueryId={QueryId}", query.QueryId);
            failed = 1;
        }

        // Collapse by DocId, exactly as the chunk path does. Two entities can carry one DocId (the
        // same corpus ingested twice), and SearchSimilar returns entities — so without this the run
        // file lists that doc id at two ranks, which is malformed TREC written silently.
        return (DocumentRanking.CollapseByDocId(results, DocumentBudget), failed);
    }

    private async Task<(IReadOnlyList<(string DocId, double Score)> Ranked, int Failed, IReadOnlyList<string> Unresolved)> RunChunksAsync(
        CorpusQuery query, Metadata headers, IReadOnlyDictionary<string, string> keyMap, CancellationToken ct)
    {
        var request = Query.Chunks<BenchmarkDocument>(d => d.Body)
            .Text(query.Text)
            .TopK((uint)(DocumentBudget * ChunkBudgetMultiplier))
            .Build();

        var chunks = new List<(string ParentKey, double Score)>();
        var failed = 0;
        try
        {
            using var call = search.SearchChunks(request, headers, cancellationToken: ct);
            await foreach (var r in call.ResponseStream.ReadAllAsync(ct))
                chunks.Add((r.ParentKey, r.Score));
        }
        catch (RpcException ex)
        {
            logger.LogWarning(ex, "SearchChunks failed for QueryId={QueryId}", query.QueryId);
            failed = 1;
        }

        var aggregated = MaxPassageAggregator.Aggregate(chunks, keyMap, DocumentBudget);
        return (aggregated.Ranked, failed, aggregated.UnresolvedParentKeys);
    }

    private static List<CorpusQuery> LoadQueries(string corpusPath)
    {
        var queries = new List<CorpusQuery>();

        var beirQueries = Path.Combine(corpusPath, "beir", "queries.jsonl");
        if (File.Exists(beirQueries))
        {
            using var reader = new StreamReader(beirQueries);
            queries.AddRange(JsonlCorpusParser.ParseQueries(reader));
        }

        var freshStackQueries = Path.Combine(corpusPath, "freshstack", "queries.jsonl");
        if (File.Exists(freshStackQueries))
        {
            using var reader = new StreamReader(freshStackQueries);
            queries.AddRange(JsonlCorpusParser.ParseQueries(reader));
        }

        return queries;
    }
}
