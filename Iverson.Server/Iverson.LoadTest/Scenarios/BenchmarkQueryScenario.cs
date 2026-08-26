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

        return (results.OrderByDescending(r => r.Score).Take(DocumentBudget).ToList(), failed);
    }

    private async Task<(IReadOnlyList<(string DocId, double Score)> Ranked, int Failed)> RunChunksAsync(
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

        return (MaxPassageAggregator.Aggregate(chunks, keyMap, DocumentBudget), failed);
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
