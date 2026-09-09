using System.Text.Json.Nodes;
using FluentAssertions;
using Iverson.LoadTest.Benchmark;
using Iverson.LoadTest.Scenarios;
using Xunit;

namespace Iverson.LoadTest.Tests.Benchmark;

public class BenchmarkAggregateScenarioTests
{
    private const string ConfigLabel = "arm";

    private static string TempDir(string suffix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"benchmark-aggregate-test-{Guid.NewGuid()}-{suffix}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task WriteHitsAsync(string path, params (string QueryId, string ParentKey, int Rank, double Score)[] rows)
    {
        var lines = new List<string> { "queryId\tparentKey\trank\tscore" };
        lines.AddRange(rows.Select(r =>
            $"{r.QueryId}\t{r.ParentKey}\t{r.Rank}\t{r.Score.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
        await File.WriteAllLinesAsync(path, lines);
    }

    private static async Task WritePoolSidecarAsync(string path, string? composite)
    {
        var json = composite is null
            ? "{}"
            : $$"""{"composite":"{{composite}}"}""";
        await File.WriteAllTextAsync(path, json);
    }

    private static CommandFlags Flags(string keyMapPath, string hitsPath, string outputDir, string configLabel = ConfigLabel, double beta = 0) =>
        new()
        {
            KeyMapPath  = keyMapPath,
            HitsPath    = hitsPath,
            OutputDir   = outputDir,
            ConfigLabel = configLabel,
            Beta        = beta,
        };

    [Fact]
    public async Task RunAsync_MissingKeyMapPath_Throws()
    {
        var dir = TempDir("no-keymap");
        try
        {
            var flags = Flags("", Path.Combine(dir, "x.chunks.hits.tsv"), dir);
            var act = () => new BenchmarkAggregateScenario().RunAsync(flags);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task RunAsync_MissingHitsPath_Throws()
    {
        var dir = TempDir("no-hits");
        try
        {
            var flags = Flags(Path.Combine(dir, "keymap.json"), "", dir);
            var act = () => new BenchmarkAggregateScenario().RunAsync(flags);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task RunAsync_MissingOutputDir_Throws()
    {
        var dir = TempDir("no-outdir");
        try
        {
            var flags = Flags(Path.Combine(dir, "keymap.json"), Path.Combine(dir, "x.chunks.hits.tsv"), "");
            var act = () => new BenchmarkAggregateScenario().RunAsync(flags);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task RunAsync_MissingConfigLabel_Throws()
    {
        var dir = TempDir("no-label");
        try
        {
            var flags = Flags(Path.Combine(dir, "keymap.json"), Path.Combine(dir, "x.chunks.hits.tsv"), dir, configLabel: "");
            var act = () => new BenchmarkAggregateScenario().RunAsync(flags);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task RunAsync_HitsPathWithoutExpectedSuffix_Throws()
    {
        var dir = TempDir("bad-suffix");
        try
        {
            var keyMapPath = Path.Combine(dir, "keymap.json");
            await KeyMap.SaveAsync(new Dictionary<string, string>(), keyMapPath);
            var hitsPath = Path.Combine(dir, "pool.txt"); // wrong suffix
            await WriteHitsAsync(hitsPath);

            var flags = Flags(keyMapPath, hitsPath, dir);
            var act = () => new BenchmarkAggregateScenario().RunAsync(flags);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task RunAsync_MissingPoolSidecar_Throws()
    {
        var dir = TempDir("no-sidecar");
        try
        {
            var keyMapPath = Path.Combine(dir, "keymap.json");
            await KeyMap.SaveAsync(new Dictionary<string, string> { ["k1"] = "doc1" }, keyMapPath);

            var hitsPath = Path.Combine(dir, "pool.chunks.hits.tsv");
            await WriteHitsAsync(hitsPath, ("q1", "k1", 1, 0.5));
            // Deliberately do NOT write pool.meta.json.

            var flags = Flags(keyMapPath, hitsPath, dir);
            var act = () => new BenchmarkAggregateScenario().RunAsync(flags);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task RunAsync_PoolSidecarMissingComposite_Throws()
    {
        var dir = TempDir("no-composite");
        try
        {
            var keyMapPath = Path.Combine(dir, "keymap.json");
            await KeyMap.SaveAsync(new Dictionary<string, string> { ["k1"] = "doc1" }, keyMapPath);

            var hitsPath = Path.Combine(dir, "pool.chunks.hits.tsv");
            await WriteHitsAsync(hitsPath, ("q1", "k1", 1, 0.5));
            await WritePoolSidecarAsync(Path.Combine(dir, "pool.meta.json"), composite: null);

            var flags = Flags(keyMapPath, hitsPath, dir);
            var act = () => new BenchmarkAggregateScenario().RunAsync(flags);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task RunAsync_BetaZero_WritesMaxPassageRankingAndCopiesComposite()
    {
        var dir = TempDir("happy-path");
        try
        {
            var keyMapPath = Path.Combine(dir, "keymap.json");
            await KeyMap.SaveAsync(
                new Dictionary<string, string> { ["k1"] = "doc1", ["k2"] = "doc2" }, keyMapPath);

            var hitsPath = Path.Combine(dir, "pool.chunks.hits.tsv");
            await WriteHitsAsync(
                hitsPath,
                ("q1", "k1", 1, 0.9),
                ("q1", "k2", 2, 0.5),
                ("q2", "k2", 1, 0.7));
            await WritePoolSidecarAsync(Path.Combine(dir, "pool.meta.json"), composite: "abc123");

            var flags = Flags(keyMapPath, hitsPath, dir, configLabel: "beta0");
            await new BenchmarkAggregateScenario().RunAsync(flags);

            var runPath = Path.Combine(dir, "beta0.chunks.trec");
            File.Exists(runPath).Should().BeTrue();
            var lines = await File.ReadAllLinesAsync(runPath);
            lines.Should().Equal(
                "q1 Q0 doc1 1 0.900000 beta0",
                "q1 Q0 doc2 2 0.500000 beta0",
                "q2 Q0 doc2 1 0.700000 beta0");

            var sidecarPath = Path.Combine(dir, "beta0.meta.json");
            File.Exists(sidecarPath).Should().BeTrue();
            var sidecar = JsonNode.Parse(await File.ReadAllTextAsync(sidecarPath))!;
            sidecar["composite"]!.GetValue<string>().Should().Be("abc123");
            sidecar["configLabel"]!.GetValue<string>().Should().Be("beta0");
            sidecar["beta"]!.GetValue<double>().Should().Be(0);
            sidecar["hitsPath"]!.GetValue<string>().Should().Be(hitsPath);
            sidecar["aggregatorComposite"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // Step 4's mandated test: exact-score ties break by insertion order, which is dump order --
    // CollapseByDocId's OrderByDescending is stable, and the dictionary it sorts is populated in the
    // order MaxPassageAggregator iterates its input, which is this scenario's dump-file row order.
    // Any reordering upstream (e.g. sorting the hits, or GroupBy losing order) would silently swap
    // these two documents and pass every other assertion in this file.
    [Fact]
    public async Task RunAsync_EqualScoreDocuments_AppearInDumpOrder()
    {
        var dir = TempDir("equal-score");
        try
        {
            var keyMapPath = Path.Combine(dir, "keymap.json");
            await KeyMap.SaveAsync(
                new Dictionary<string, string> { ["k-a"] = "doc-a", ["k-b"] = "doc-b" }, keyMapPath);

            var hitsPath = Path.Combine(dir, "pool.chunks.hits.tsv");
            // doc-a's hit appears first in the dump; both score exactly 0.5.
            await WriteHitsAsync(
                hitsPath,
                ("q1", "k-a", 1, 0.5),
                ("q1", "k-b", 2, 0.5));
            await WritePoolSidecarAsync(Path.Combine(dir, "pool.meta.json"), composite: "abc123");

            var flags = Flags(keyMapPath, hitsPath, dir, configLabel: "tie");
            await new BenchmarkAggregateScenario().RunAsync(flags);

            var lines = await File.ReadAllLinesAsync(Path.Combine(dir, "tie.chunks.trec"));
            lines.Should().Equal(
                "q1 Q0 doc-a 1 0.500000 tie",
                "q1 Q0 doc-b 2 0.500000 tie");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task RunAsync_PreservesQueryFirstAppearanceOrder_NotSortedQueryId()
    {
        var dir = TempDir("query-order");
        try
        {
            var keyMapPath = Path.Combine(dir, "keymap.json");
            await KeyMap.SaveAsync(new Dictionary<string, string> { ["k1"] = "doc1" }, keyMapPath);

            var hitsPath = Path.Combine(dir, "pool.chunks.hits.tsv");
            // "q2" appears in the dump before "q1" -- alphabetical sort would flip this.
            await WriteHitsAsync(
                hitsPath,
                ("q2", "k1", 1, 0.4),
                ("q1", "k1", 1, 0.6));
            await WritePoolSidecarAsync(Path.Combine(dir, "pool.meta.json"), composite: "abc123");

            var flags = Flags(keyMapPath, hitsPath, dir, configLabel: "order");
            await new BenchmarkAggregateScenario().RunAsync(flags);

            var lines = await File.ReadAllLinesAsync(Path.Combine(dir, "order.chunks.trec"));
            lines.Should().Equal(
                "q2 Q0 doc1 1 0.400000 order",
                "q1 Q0 doc1 1 0.600000 order");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // The sidecar is located by deriving from --hits-path, not --config-label: this is what lets a
    // different-label replay against the same pool (Phase 2's β arms) still find the pool's composite.
    [Fact]
    public async Task RunAsync_SidecarDerivedFromHitsPath_NotConfigLabel()
    {
        var poolDir = TempDir("pool-dir");
        var outDir  = TempDir("out-dir");
        try
        {
            var keyMapPath = Path.Combine(poolDir, "keymap.json");
            await KeyMap.SaveAsync(new Dictionary<string, string> { ["k1"] = "doc1" }, keyMapPath);

            var hitsPath = Path.Combine(poolDir, "pool-label.chunks.hits.tsv");
            await WriteHitsAsync(hitsPath, ("q1", "k1", 1, 0.5));
            await WritePoolSidecarAsync(Path.Combine(poolDir, "pool-label.meta.json"), composite: "pool-build");

            // Different --config-label AND different --output-dir than the pool run.
            var flags = Flags(keyMapPath, hitsPath, outDir, configLabel: "beta003-arm");
            await new BenchmarkAggregateScenario().RunAsync(flags);

            File.Exists(Path.Combine(outDir, "beta003-arm.chunks.trec")).Should().BeTrue();
            var sidecar = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(outDir, "beta003-arm.meta.json")))!;
            sidecar["composite"]!.GetValue<string>().Should().Be("pool-build");
            sidecar["configLabel"]!.GetValue<string>().Should().Be("beta003-arm");
        }
        finally
        {
            Directory.Delete(poolDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_UnresolvedParentKey_WritesRunFileThenThrows()
    {
        var dir = TempDir("unresolved");
        try
        {
            var keyMapPath = Path.Combine(dir, "keymap.json");
            // "missing-key" is deliberately absent from the key map.
            await KeyMap.SaveAsync(new Dictionary<string, string> { ["k1"] = "doc1" }, keyMapPath);

            var hitsPath = Path.Combine(dir, "pool.chunks.hits.tsv");
            await WriteHitsAsync(
                hitsPath,
                ("q1", "k1", 1, 0.5),
                ("q1", "missing-key", 2, 0.4));
            await WritePoolSidecarAsync(Path.Combine(dir, "pool.meta.json"), composite: "abc123");

            var flags = Flags(keyMapPath, hitsPath, dir, configLabel: "partial");
            var act = () => new BenchmarkAggregateScenario().RunAsync(flags);
            await act.Should().ThrowAsync<InvalidOperationException>();

            var runPath = Path.Combine(dir, "partial.chunks.trec");
            File.Exists(runPath).Should().BeTrue("the run file is written before the fail-loud throw, for diagnosis");
            var lines = await File.ReadAllLinesAsync(runPath);
            lines.Should().Equal("q1 Q0 doc1 1 0.500000 partial");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task RunAsync_Beta_IsPassedThroughToAggregator()
    {
        var dir = TempDir("beta-nonzero");
        try
        {
            var keyMapPath = Path.Combine(dir, "keymap.json");
            await KeyMap.SaveAsync(
                new Dictionary<string, string> { ["k1"] = "doc1" }, keyMapPath);

            // Same parent, multiple chunks -- CollapseByDocIdWithTail's tail-sum credit only kicks in
            // above beta=0, so this distinguishes "beta was plumbed through" from "beta was ignored".
            var hitsPath = Path.Combine(dir, "pool.chunks.hits.tsv");
            await WriteHitsAsync(
                hitsPath,
                ("q1", "k1", 1, 0.9),
                ("q1", "k1", 2, 0.3));
            await WritePoolSidecarAsync(Path.Combine(dir, "pool.meta.json"), composite: "abc123");

            var flags = Flags(keyMapPath, hitsPath, dir, configLabel: "beta05", beta: 0.5);
            await new BenchmarkAggregateScenario().RunAsync(flags);

            var lines = await File.ReadAllLinesAsync(Path.Combine(dir, "beta05.chunks.trec"));
            // score = max(0.9) + 0.5 * tail(0.3) = 1.05
            lines.Should().Equal("q1 Q0 doc1 1 1.050000 beta05");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
