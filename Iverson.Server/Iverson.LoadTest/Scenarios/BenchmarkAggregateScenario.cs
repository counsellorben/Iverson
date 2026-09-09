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

        var hits = new List<(string QueryId, string ParentKey, double Score)>(lines.Length - 1);
        for (var i = 1; i < lines.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (lines[i].Length == 0) continue;

            var fields = lines[i].Split('\t');
            hits.Add((
                fields[queryIdCol],
                fields[parentKeyCol],
                double.Parse(fields[scoreCol], CultureInfo.InvariantCulture)));
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
