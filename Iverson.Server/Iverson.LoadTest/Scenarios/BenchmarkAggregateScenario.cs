using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Iverson.LoadTest.Benchmark;

namespace Iverson.LoadTest.Scenarios;

/// <summary>
/// Replays a <see cref="ChunkHitDumpWriter"/> dump (<c>&lt;label&gt;.chunks.hits.tsv</c>, written by
/// <see cref="BenchmarkQueryScenario"/>) through <see cref="MaxPassageAggregator"/> at a caller-chosen
/// <c>--beta</c>, and writes the resulting document ranking as a TREC run file. This is what lets the
/// chunk-coverage sweep compare several tail-sum weightings against one already-collected pool of raw
/// SearchChunks hits, without re-issuing a single search RPC (spec §3 — this is the offline replay path
/// alongside <see cref="BenchmarkQueryScenario"/>'s in-run one).
///
/// <see cref="MaxPassageAggregator.Aggregate(System.Collections.Generic.IEnumerable{ValueTuple{string,double}},System.Collections.Generic.IReadOnlyDictionary{string,string},int,double)"/>
/// owns key-map resolution and unresolved-parent handling — this scenario groups rows and calls it, and
/// does not reimplement either.
///
/// Unlike <see cref="BenchmarkQueryScenario"/>, this command cannot call the API's <c>GET /build</c> and
/// still be offline, so it does not attempt to: it copies the pool run's own <c>composite</c> out of the
/// sidecar next to <c>--hits-path</c> verbatim, so every β arm replayed from one pool agrees on build
/// attribution and <c>report.py</c> never prints <c>BUILD MISMATCH</c> across them. It separately records
/// a fingerprint of its own assembly (which is where <see cref="MaxPassageAggregator"/> and
/// <see cref="DocumentRanking"/> live) under a second key, purely as documentation — <c>report.py</c>
/// reads only <c>composite</c> — so a re-aggregation run under a changed aggregator is at least
/// distinguishable after the fact.
/// </summary>
public sealed class BenchmarkAggregateScenario
{
    // Matches BenchmarkQueryScenario.cs:42 exactly. Deliberately duplicated rather than shared: if the
    // two ever drift, the beta=0 identity check (this plan's Task 5 Step 3) fails, because a different
    // truncation limit produces a different run file -- the mismatch is self-checking.
    private const int DocumentBudget = 50;

    private const string HitsSuffix = ".chunks.hits.tsv";

    private static readonly JsonSerializerOptions SidecarWriteOptions = new() { WriteIndented = true };

    public async Task RunAsync(CommandFlags flags, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(flags.KeyMapPath))
        {
            Console.Error.WriteLine("benchmark-aggregate requires --key-map-path.");
            throw new InvalidOperationException("--key-map-path was not provided.");
        }
        if (string.IsNullOrWhiteSpace(flags.HitsPath))
        {
            Console.Error.WriteLine("benchmark-aggregate requires --hits-path.");
            throw new InvalidOperationException("--hits-path was not provided.");
        }
        if (string.IsNullOrWhiteSpace(flags.OutputDir))
        {
            Console.Error.WriteLine("benchmark-aggregate requires --output-dir.");
            throw new InvalidOperationException("--output-dir was not provided.");
        }
        if (string.IsNullOrWhiteSpace(flags.ConfigLabel))
        {
            Console.Error.WriteLine("benchmark-aggregate requires --config-label.");
            throw new InvalidOperationException("--config-label was not provided.");
        }

        // Checked HERE, before a single file is opened. A NaN beta skips CollapseByDocIdWithTail's
        // `beta == 0` short-circuit, scores every document NaN, writes a complete-looking run file of
        // NaN rows, and only then fails when System.Text.Json refuses NaN for the sidecar -- leaving a
        // poisoned run file with no sidecar to disown it. A negative beta is not a weighting the spec
        // defines: it PENALISES a document for having more good chunks.
        if (!double.IsFinite(flags.Beta) || flags.Beta < 0)
        {
            Console.Error.WriteLine(
                $"REFUSING: --beta '{flags.Beta.ToString(CultureInfo.InvariantCulture)}' is not a finite, " +
                "non-negative number -- the tail-sum weight must be finite and >= 0.");
            throw new InvalidOperationException("--beta must be finite and non-negative.");
        }

        // Derived from --hits-path, NOT --config-label: the pool run's own sidecar sits beside the pool
        // run's own label, which may differ from this replay's --config-label (Phase 2's β arms carry
        // different labels against the same pool), and the identity check (Task 5) runs at the pool's
        // own label but in a different --output-dir. Deriving from --hits-path is what makes both work.
        if (!flags.HitsPath.EndsWith(HitsSuffix, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"benchmark-aggregate: --hits-path '{flags.HitsPath}' does not end with '{HitsSuffix}' " +
                "-- cannot derive the pool run's sidecar path from it.");
            throw new InvalidOperationException("--hits-path does not have the expected suffix.");
        }
        var poolSidecarPath = flags.HitsPath[..^HitsSuffix.Length] + ".meta.json";

        if (!File.Exists(poolSidecarPath))
        {
            Console.Error.WriteLine(
                $"REFUSING: no pool sidecar at {poolSidecarPath} (derived from --hits-path) -- a run " +
                "that cannot be attributed to a build must not start.");
            throw new InvalidOperationException("pool sidecar not found for --hits-path.");
        }

        var poolSidecarJson = JsonNode.Parse(await File.ReadAllTextAsync(poolSidecarPath, ct))
            ?? throw new InvalidOperationException($"{poolSidecarPath} could not be parsed.");

        var poolComposite = poolSidecarJson["composite"];
        if (poolComposite is null)
        {
            Console.Error.WriteLine(
                $"REFUSING: {poolSidecarPath} has no \"composite\" -- a run that cannot be attributed " +
                "to a build must not start.");
            throw new InvalidOperationException("pool sidecar had no composite for run attribution.");
        }

        // The dump holds the RAW SearchChunks scores, captured before the cross-encoder rescored the
        // max-passage winners -- so a reranked pool run's own .chunks.trec was built from reranker
        // scores that never entered the dump. Replaying that dump does not reproduce the pool run at
        // beta=0, and the two rankings are not comparable at any beta. BenchmarkQueryScenario writes
        // this key as JSON null on an un-reranked run, which indexes to a null JsonNode exactly as an
        // absent key does, so both non-reranked shapes pass.
        if (poolSidecarJson["reranker"] is not null)
        {
            Console.Error.WriteLine(
                $"REFUSING: {poolSidecarPath} records a reranker -- that run's .chunks.trec came from " +
                "cross-encoder scores, while the dump holds the raw pre-rerank chunk scores. The replay " +
                "would not be comparable to the pool run at any beta.");
            throw new InvalidOperationException("pool sidecar records a reranker; the dump is pre-rerank.");
        }

        var keyMap = await KeyMap.LoadAsync(flags.KeyMapPath, ct);
        Console.WriteLine($"[benchmark-aggregate] Loaded key map ({keyMap.Count:N0} entries) from {flags.KeyMapPath}");

        // Row order is load-bearing end to end: read in file order, group with LINQ GroupBy (which
        // preserves both key first-appearance order and within-group element order), and never sort.
        // Exact-score ties break by insertion order inside CollapseByDocId's dictionary
        // (DocumentRanking.cs:32-44), so the in-run tie order IS the Qdrant stream order the original
        // dump captured -- any reordering here would fail the beta=0 byte identity while looking like an
        // aggregator defect.
        var hits = await ReadHitsAsync(flags.HitsPath, ct);
        Console.WriteLine($"[benchmark-aggregate] Loaded {hits.Count:N0} chunk hit(s) from {flags.HitsPath}");

        // A run that BenchmarkQueryScenario REFUSED (failed RPCs, or parent keys absent from the key
        // map) still leaves a complete-looking dump behind, under exactly the filenames an accepted run
        // uses and with the same composite -- and a beta=0 replay of it passes the identity check. The
        // only thing on disk that separates the two is this field: BenchmarkQueryScenario writes
        // `queryCount` into the sidecar ONLY after both of its fail-loud checks have passed.
        var declaredQueryCount = poolSidecarJson["queryCount"]?.GetValue<int>();
        var dumpQueryCount     = hits.Select(h => h.QueryId).Distinct(StringComparer.Ordinal).Count();
        if (declaredQueryCount is { } declared)
        {
            if (declared != dumpQueryCount)
            {
                Console.Error.WriteLine(
                    $"REFUSING: {poolSidecarPath} records queryCount={declared:N0}, but {flags.HitsPath} " +
                    $"holds {dumpQueryCount:N0} distinct query id(s) -- the dump is from an incomplete " +
                    "run and must not be replayed.");
                throw new InvalidOperationException(
                    $"pool sidecar queryCount ({declared}) does not match the dump's distinct query id " +
                    $"count ({dumpQueryCount}) -- the dump is from an incomplete run.");
            }
        }
        else
        {
            Console.Error.WriteLine(
                "======================================================================\n" +
                $"WARNING: {poolSidecarPath}\n" +
                "has no \"queryCount\" -- it predates the run-completeness field, so this replay CANNOT\n" +
                "verify that\n" +
                $"  {flags.HitsPath}\n" +
                "came from a run that finished. A run BenchmarkQueryScenario REFUSED leaves a dump that\n" +
                "is indistinguishable from an accepted one. Confirm by hand that this dump is the\n" +
                "accepted run's before scoring anything derived from it.\n" +
                "======================================================================");
        }

        var results = new List<(string QueryId, IReadOnlyList<(string DocId, double Score)> Ranked)>();
        var unresolvedParents = new HashSet<string>(StringComparer.Ordinal);

        // GroupBy over a List preserves first-appearance key order and within-group source order --
        // emitting queries in the dump's own order, not sorted QueryId order, is the point.
        foreach (var group in hits.GroupBy(h => h.QueryId))
        {
            var chunks = group.Select(h => (h.ParentKey, h.Score));
            var aggregated = MaxPassageAggregator.Aggregate(chunks, keyMap, DocumentBudget, flags.Beta);
            results.Add((group.Key, aggregated.Ranked));
            foreach (var parentKey in aggregated.UnresolvedParentKeys)
                unresolvedParents.Add(parentKey);
        }

        var runPath = Path.Combine(flags.OutputDir, $"{flags.ConfigLabel}.chunks.trec");
        await TrecRunWriter.WriteAsync(runPath, results, flags.ConfigLabel, ct);
        Console.WriteLine($"[benchmark-aggregate] Wrote {runPath}");

        Directory.CreateDirectory(flags.OutputDir);
        var sidecar = new JsonObject
        {
            ["configLabel"]         = flags.ConfigLabel,
            ["composite"]           = poolComposite.DeepClone(),
            ["aggregatorComposite"] = ComputeAggregatorComposite(),
            ["beta"]                = flags.Beta,
            ["hitsPath"]            = flags.HitsPath,
            ["recordedAtUtc"]       = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            // Carried through from the pool sidecar rather than recomputed: SearchChunks top_k is
            // 50 x this, so it is the parameter the whole result is scoped to, and a replayed arm whose
            // sidecar omits it cannot be compared to anything. Null when the pool sidecar lacks it.
            ["chunkBudgetMultiplier"] = poolSidecarJson["chunkBudgetMultiplier"]?.DeepClone(),
        };
        var sidecarPath = Path.Combine(flags.OutputDir, $"{flags.ConfigLabel}.meta.json");
        await File.WriteAllTextAsync(sidecarPath, sidecar.ToJsonString(SidecarWriteOptions), ct);
        Console.WriteLine($"[benchmark-aggregate] Wrote {sidecarPath}");

        // Same fail-loud contract as BenchmarkQueryScenario: the run files are written first (useful for
        // diagnosis), and only then does the command fail, so an incomplete run file is never mistaken
        // for a complete one.
        if (unresolvedParents.Count > 0)
            throw new InvalidOperationException(
                $"[benchmark-aggregate] {unresolvedParents.Count:N0} parent key(s) in {flags.HitsPath} " +
                $"are absent from the key map at {flags.KeyMapPath} -- the run file above is built from " +
                $"an unknown corpus and must not be scored. First few: {string.Join(", ", unresolvedParents.Take(5))}.");
    }

    /// <summary>
    /// Reads the hits dump by the TSV's own column names (<c>queryId</c>, <c>parentKey</c>, <c>score</c>)
    /// -- the contract Task 2's <see cref="ChunkHitDumpWriter"/> defines and this file is the only other
    /// reader of -- rather than by fixed column position, and preserves file order exactly.
    /// </summary>
    private static async Task<List<(string QueryId, string ParentKey, double Score)>> ReadHitsAsync(
        string path, CancellationToken ct)
    {
        var lines = await File.ReadAllLinesAsync(path, ct);
        if (lines.Length == 0)
            throw new InvalidOperationException($"{path} is empty -- expected a header row.");

        var columns = lines[0].Split('\t');
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < columns.Length; i++)
            index[columns[i]] = i;

        foreach (var required in new[] { "queryId", "parentKey", "score" })
        {
            if (!index.ContainsKey(required))
                throw new InvalidOperationException($"{path}: header is missing required column '{required}'.");
        }

        var queryIdCol   = index["queryId"];
        var parentKeyCol = index["parentKey"];
        var scoreCol     = index["score"];
        // Optional only so a dump written without the column stays readable; when it IS present it is
        // checked, because it is the file's own record of the order this reader depends on.
        var rankCol = index.TryGetValue("rank", out var rankIndex) ? rankIndex : -1;

        var hits = new List<(string QueryId, string ParentKey, double Score)>(lines.Length - 1);
        var rowsPerQuery = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 1; i < lines.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (lines[i].Length == 0) continue;

            var fields  = lines[i].Split('\t');
            var queryId = fields[queryIdCol];

            // ChunkHitDumpWriter writes `rank` 1-based per query in the order the hits were supplied,
            // and this reader rebuilds the ranking from FILE ORDER alone -- it never sorts. Nothing used
            // to check that the two agree, so a dump that had been sorted, filtered or concatenated read
            // as valid and produced a plausible run file from the wrong order. Cross-check them.
            rowsPerQuery.TryGetValue(queryId, out var rowsSoFar);
            rowsPerQuery[queryId] = ++rowsSoFar;
            if (rankCol >= 0)
            {
                if (!int.TryParse(fields[rankCol], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rank))
                    throw new InvalidOperationException(
                        $"{path}:{i + 1}: rank '{fields[rankCol]}' is not an integer.");
                if (rank != rowsSoFar)
                    throw new InvalidOperationException(
                        $"{path}:{i + 1}: rank {rank} disagrees with file order (this is row {rowsSoFar} " +
                        $"of query '{queryId}') -- the dump has been reordered or edited, and this reader " +
                        "rebuilds the ranking from file order alone.");
            }

            hits.Add((
                queryId,
                fields[parentKeyCol],
                double.Parse(fields[scoreCol], NumberStyles.Float, CultureInfo.InvariantCulture)));
        }

        return hits;
    }

    /// <summary>
    /// A SHA-256 fingerprint of this process's own assembly -- the one <see cref="MaxPassageAggregator"/>
    /// and <see cref="DocumentRanking"/> are compiled into -- so two aggregate runs against the same pool
    /// under different aggregator code are distinguishable after the fact. This is documentation, not an
    /// enforced check: <c>report.py</c> reads only <c>composite</c>, never this key.
    /// </summary>
    private static string ComputeAggregatorComposite()
    {
        var path = Assembly.GetExecutingAssembly().Location;
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream))[..16].ToLowerInvariant();
    }
}
