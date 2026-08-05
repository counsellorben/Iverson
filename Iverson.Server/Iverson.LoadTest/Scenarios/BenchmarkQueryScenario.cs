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
        foreach (var query in queries)
        {
            ct.ThrowIfCancellationRequested();

            var identity = identities.PickRandom();
            var headers  = new Metadata().WithActingUser(await identity.GetTokenAsync(ct));

            similarResults.Add((query.QueryId, await RunSimilarAsync(query, headers, ct)));
            chunksResults.Add((query.QueryId, await RunChunksAsync(query, headers, keyMap, ct)));

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
    }

    private async Task<IReadOnlyList<(string DocId, double Score)>> RunSimilarAsync(
        CorpusQuery query, Metadata headers, CancellationToken ct)
    {
        var request = Query.Similar<BenchmarkDocument>(d => d.Body)
            .Text(query.Text)
            .TopK(DocumentBudget)
            .Build();

        var results = new List<(string DocId, double Score)>();
        try
        {
            using var call = search.SearchSimilar(request, headers, cancellationToken: ct);
            await foreach (var r in call.ResponseStream.ReadAllAsync(ct))
            {
                // Data is a Struct of camelCase STRING fields taken from the Qdrant payload (P9);
                // it is not a deserialized entity, and StructConverter is internal to Core (P10).
                var docId = r.Data.Fields["docId"].StringValue;
                results.Add((docId, r.Score));
            }
        }
        catch (RpcException ex)
        {
            logger.LogWarning(ex, "SearchSimilar failed for QueryId={QueryId}", query.QueryId);
        }

        return results.OrderByDescending(r => r.Score).Take(DocumentBudget).ToList();
    }

    private async Task<IReadOnlyList<(string DocId, double Score)>> RunChunksAsync(
        CorpusQuery query, Metadata headers, IReadOnlyDictionary<string, string> keyMap, CancellationToken ct)
    {
        var request = Query.Chunks<BenchmarkDocument>(d => d.Body)
            .Text(query.Text)
            .TopK((uint)(DocumentBudget * ChunkBudgetMultiplier))
            .Build();

        var chunks = new List<(string ParentKey, double Score)>();
        try
        {
            using var call = search.SearchChunks(request, headers, cancellationToken: ct);
            await foreach (var r in call.ResponseStream.ReadAllAsync(ct))
                chunks.Add((r.ParentKey, r.Score));
        }
        catch (RpcException ex)
        {
            logger.LogWarning(ex, "SearchChunks failed for QueryId={QueryId}", query.QueryId);
        }

        return MaxPassageAggregator.Aggregate(chunks, keyMap, DocumentBudget);
    }

    private static List<CorpusQuery> LoadQueries(string corpusPath)
    {
        var queries = new List<CorpusQuery>();

        var beirQueries = Path.Combine(corpusPath, "beir", "queries.jsonl");
        if (File.Exists(beirQueries))
        {
            using var reader = new StreamReader(beirQueries);
            queries.AddRange(BeirCorpusParser.ParseQueries(reader));
        }

        var freshStackQueries = Path.Combine(corpusPath, "freshstack", "queries.jsonl");
        if (File.Exists(freshStackQueries))
        {
            using var reader = new StreamReader(freshStackQueries);
            queries.AddRange(FreshStackCorpusParser.ParseQueries(reader));
        }

        return queries;
    }
}
