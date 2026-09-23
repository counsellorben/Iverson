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

    private static async Task WritePoolSidecarAsync(
        string path, string? composite, string? extraJson = null)
    {
        var fields = new List<string>();
        if (composite is not null) fields.Add("\"composite\":\"" + composite + "\"");
        if (extraJson is not null) fields.Add(extraJson);
        await File.WriteAllTextAsync(path, "{" + string.Join(",", fields) + "}");
    }

    private static CommandFlags Flags(
        string keyMapPath, string hitsPath, string outputDir, string configLabel = ConfigLabel,
        double beta = 0, string scoresPath = "") =>
        new()
        {
            KeyMapPath  = keyMapPath,
            HitsPath    = hitsPath,
            OutputDir   = outputDir,
            ConfigLabel = configLabel,
            Beta        = beta,
            ScoresPath  = scoresPath,
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

    // ── F2: a beta that is not a finite, non-negative number ─────────────────────────────────

    // NaN skips CollapseByDocIdWithTail's `beta == 0` short-circuit, scores every document NaN, and
    // writes a complete-looking run file of NaN rows before System.Text.Json refuses NaN for the
    // sidecar -- a poisoned run file with no sidecar to disown it. The refusal must land before any
    // file is opened, which is what the empty-directory assertion pins.
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1.0)]
    public async Task RunAsync_BetaNotFiniteOrNegative_RefusesBeforeWritingAnyFile(double beta)
    {
        var poolDir = TempDir("bad-beta-pool");
        var outDir  = TempDir("bad-beta-out");
        try
        {
            var keyMapPath = Path.Combine(poolDir, "keymap.json");
            await KeyMap.SaveAsync(new Dictionary<string, string> { ["k1"] = "doc1" }, keyMapPath);

            var hitsPath = Path.Combine(poolDir, "pool.chunks.hits.tsv");
            await WriteHitsAsync(hitsPath, ("q1", "k1", 1, 0.9), ("q1", "k1", 2, 0.3));
            await WritePoolSidecarAsync(Path.Combine(poolDir, "pool.meta.json"), composite: "abc123");

            var flags = Flags(keyMapPath, hitsPath, outDir, configLabel: "bad", beta: beta);
            var act = () => new BenchmarkAggregateScenario().RunAsync(flags);
            await act.Should().ThrowAsync<InvalidOperationException>();

            Directory.GetFileSystemEntries(outDir).Should().BeEmpty(
                "the beta guard must run before a single output file is opened");
        }
        finally
        {
            Directory.Delete(poolDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_ValidPositiveBeta_StillWritesTheRunFile()
    {
        var dir = TempDir("good-beta");
        try
        {
            var keyMapPath = Path.Combine(dir, "keymap.json");
            await KeyMap.SaveAsync(new Dictionary<string, string> { ["k1"] = "doc1" }, keyMapPath);

            var hitsPath = Path.Combine(dir, "pool.chunks.hits.tsv");
            await WriteHitsAsync(hitsPath, ("q1", "k1", 1, 0.9), ("q1", "k1", 2, 0.3));
            await WritePoolSidecarAsync(Path.Combine(dir, "pool.meta.json"), composite: "abc123");

            await new BenchmarkAggregateScenario().RunAsync(
                Flags(keyMapPath, hitsPath, dir, configLabel: "ok", beta: 0.0358));

            var lines = await File.ReadAllLinesAsync(Path.Combine(dir, "ok.chunks.trec"));
            lines.Should().Equal("q1 Q0 doc1 1 0.910740 ok");  // 0.9 + 0.0358 * 0.3
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── F1: a refused pool run's dump must not replay as if it were an accepted one ──────────

    [Fact]
    public async Task RunAsync_SidecarQueryCountDisagreesWithDump_Refuses()
    {
        var poolDir = TempDir("qc-mismatch-pool");
        var outDir  = TempDir("qc-mismatch-out");
        try
        {
            var keyMapPath = Path.Combine(poolDir, "keymap.json");
            await KeyMap.SaveAsync(new Dictionary<string, string> { ["k1"] = "doc1" }, keyMapPath);

            var hitsPath = Path.Combine(poolDir, "pool.chunks.hits.tsv");
            // The dump holds 2 distinct query ids; the run that produced it declared 3.
            await WriteHitsAsync(hitsPath, ("q1", "k1", 1, 0.9), ("q2", "k1", 1, 0.8));
            await WritePoolSidecarAsync(
                Path.Combine(poolDir, "pool.meta.json"), composite: "abc123", extraJson: "\"queryCount\":3");

            var act = () => new BenchmarkAggregateScenario()
                .RunAsync(Flags(keyMapPath, hitsPath, outDir, configLabel: "incomplete"));
            (await act.Should().ThrowAsync<InvalidOperationException>())
                .WithMessage("*3*")   // the declared count
                .WithMessage("*2*");  // and the dump's own

            Directory.GetFileSystemEntries(outDir).Should().BeEmpty(
                "an incomplete run's dump must not produce a scoreable run file at all");
        }
        finally
        {
            Directory.Delete(poolDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_SidecarQueryCountMatchesDump_ProceedsWithoutWarning()
    {
        var dir = TempDir("qc-match");
        var stderr = Console.Error;
        var captured = new StringWriter();
        try
        {
            Console.SetError(captured);

            var keyMapPath = Path.Combine(dir, "keymap.json");
            await KeyMap.SaveAsync(new Dictionary<string, string> { ["k1"] = "doc1" }, keyMapPath);

            var hitsPath = Path.Combine(dir, "pool.chunks.hits.tsv");
            await WriteHitsAsync(hitsPath, ("q1", "k1", 1, 0.9), ("q2", "k1", 1, 0.8));
            await WritePoolSidecarAsync(
                Path.Combine(dir, "pool.meta.json"), composite: "abc123", extraJson: "\"queryCount\":2");

            await new BenchmarkAggregateScenario()
                .RunAsync(Flags(keyMapPath, hitsPath, dir, configLabel: "complete"));

            File.Exists(Path.Combine(dir, "complete.chunks.trec")).Should().BeTrue();
            captured.ToString().Should().NotContain("queryCount", "a verified dump warns about nothing");
        }
        finally
        {
            Console.SetError(stderr);
            Directory.Delete(dir, recursive: true);
        }
    }

    // The accepted Phase 1 pool run predates the field, so this path must WARN and proceed -- if it
    // refused, the branch's own byte-identity check could no longer be re-run.
    [Fact]
    public async Task RunAsync_SidecarWithoutQueryCount_WarnsButStillWritesTheRunFile()
    {
        var dir = TempDir("qc-absent");
        var stderr = Console.Error;
        var captured = new StringWriter();
        try
        {
            Console.SetError(captured);

            var keyMapPath = Path.Combine(dir, "keymap.json");
            await KeyMap.SaveAsync(new Dictionary<string, string> { ["k1"] = "doc1" }, keyMapPath);

            var hitsPath = Path.Combine(dir, "pool.chunks.hits.tsv");
            await WriteHitsAsync(hitsPath, ("q1", "k1", 1, 0.9));
            await WritePoolSidecarAsync(Path.Combine(dir, "pool.meta.json"), composite: "abc123");

            await new BenchmarkAggregateScenario()
                .RunAsync(Flags(keyMapPath, hitsPath, dir, configLabel: "legacy"));

            var lines = await File.ReadAllLinesAsync(Path.Combine(dir, "legacy.chunks.trec"));
            lines.Should().Equal("q1 Q0 doc1 1 0.900000 legacy");

            var warning = captured.ToString();
            warning.Should().Contain("queryCount");
            warning.Should().Contain(hitsPath, "the warning must name the dump it cannot verify");
        }
        finally
        {
            Console.SetError(stderr);
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── F4a: a reranked pool's .chunks.trec did not come from these raw scores ───────────────

    [Fact]
    public async Task RunAsync_PoolSidecarRecordsAReranker_Refuses()
    {
        var poolDir = TempDir("reranked-pool");
        var outDir  = TempDir("reranked-out");
        try
        {
            var keyMapPath = Path.Combine(poolDir, "keymap.json");
            await KeyMap.SaveAsync(new Dictionary<string, string> { ["k1"] = "doc1" }, keyMapPath);

            var hitsPath = Path.Combine(poolDir, "pool.chunks.hits.tsv");
            await WriteHitsAsync(hitsPath, ("q1", "k1", 1, 0.9));
            await WritePoolSidecarAsync(
                Path.Combine(poolDir, "pool.meta.json"),
                composite: "abc123",
                extraJson: "\"reranker\":{\"modelId\":\"bge-reranker-base\"}");

            var act = () => new BenchmarkAggregateScenario()
                .RunAsync(Flags(keyMapPath, hitsPath, outDir, configLabel: "rr"));
            await act.Should().ThrowAsync<InvalidOperationException>();

            Directory.GetFileSystemEntries(outDir).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(poolDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    // BenchmarkQueryScenario writes "reranker": null on an un-reranked run -- that JSON null must
    // read as "no reranker", exactly as an absent key does, or every real pool run is refused.
    [Fact]
    public async Task RunAsync_PoolSidecarWithExplicitNullReranker_Proceeds()
    {
        var dir = TempDir("null-reranker");
        try
        {
            var keyMapPath = Path.Combine(dir, "keymap.json");
            await KeyMap.SaveAsync(new Dictionary<string, string> { ["k1"] = "doc1" }, keyMapPath);

            var hitsPath = Path.Combine(dir, "pool.chunks.hits.tsv");
            await WriteHitsAsync(hitsPath, ("q1", "k1", 1, 0.9));
            await WritePoolSidecarAsync(
                Path.Combine(dir, "pool.meta.json"), composite: "abc123", extraJson: "\"reranker\":null");

            await new BenchmarkAggregateScenario()
                .RunAsync(Flags(keyMapPath, hitsPath, dir, configLabel: "plain"));

            File.Exists(Path.Combine(dir, "plain.chunks.trec")).Should().BeTrue();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── F4b: the parameter the whole result is scoped to ─────────────────────────────────────

    [Fact]
    public async Task RunAsync_CarriesChunkBudgetMultiplierThroughFromThePoolSidecar()
    {
        var dir = TempDir("cbm");
        try
        {
            var keyMapPath = Path.Combine(dir, "keymap.json");
            await KeyMap.SaveAsync(new Dictionary<string, string> { ["k1"] = "doc1" }, keyMapPath);

            var hitsPath = Path.Combine(dir, "pool.chunks.hits.tsv");
            await WriteHitsAsync(hitsPath, ("q1", "k1", 1, 0.9));
            await WritePoolSidecarAsync(
                Path.Combine(dir, "pool.meta.json"), composite: "abc123",
                extraJson: "\"chunkBudgetMultiplier\":11");

            await new BenchmarkAggregateScenario()
                .RunAsync(Flags(keyMapPath, hitsPath, dir, configLabel: "carried"));

            var sidecar = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "carried.meta.json")))!;
            sidecar["chunkBudgetMultiplier"]!.GetValue<int>().Should().Be(11);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── F4c: the dump's rank column is checked, not discarded ────────────────────────────────

    // This reader rebuilds the ranking from FILE ORDER alone. A dump that had been sorted, filtered
    // or concatenated used to read as valid and produce a plausible run file from the wrong order.
    [Fact]
    public async Task RunAsync_DumpRankDisagreesWithFileOrder_ThrowsNamingTheRow()
    {
        var dir = TempDir("bad-rank");
        try
        {
            var keyMapPath = Path.Combine(dir, "keymap.json");
            await KeyMap.SaveAsync(
                new Dictionary<string, string> { ["k1"] = "doc1", ["k2"] = "doc2" }, keyMapPath);

            var hitsPath = Path.Combine(dir, "pool.chunks.hits.tsv");
            // Row 2 of q1 claims rank 3 -- a row between them was dropped, or the file was sorted.
            await WriteHitsAsync(hitsPath, ("q1", "k1", 1, 0.9), ("q1", "k2", 3, 0.5));
            await WritePoolSidecarAsync(Path.Combine(dir, "pool.meta.json"), composite: "abc123");

            var act = () => new BenchmarkAggregateScenario()
                .RunAsync(Flags(keyMapPath, hitsPath, dir, configLabel: "reordered"));
            (await act.Should().ThrowAsync<InvalidOperationException>())
                .WithMessage("*:3:*");  // the 1-based row number of the offending line
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task RunAsync_DumpRankIsPerQuery_NotPerFile()
    {
        var dir = TempDir("per-query-rank");
        try
        {
            var keyMapPath = Path.Combine(dir, "keymap.json");
            await KeyMap.SaveAsync(new Dictionary<string, string> { ["k1"] = "doc1" }, keyMapPath);

            var hitsPath = Path.Combine(dir, "pool.chunks.hits.tsv");
            // q2's ranks restart at 1 -- a per-file running rank check would reject this valid dump.
            await WriteHitsAsync(hitsPath, ("q1", "k1", 1, 0.9), ("q1", "k1", 2, 0.5), ("q2", "k1", 1, 0.7));
            await WritePoolSidecarAsync(Path.Combine(dir, "pool.meta.json"), composite: "abc123");

            await new BenchmarkAggregateScenario()
                .RunAsync(Flags(keyMapPath, hitsPath, dir, configLabel: "perq"));

            var lines = await File.ReadAllLinesAsync(Path.Combine(dir, "perq.chunks.trec"));
            lines.Should().Equal("q1 Q0 doc1 1 0.900000 perq", "q2 Q0 doc1 1 0.700000 perq");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── F5: the opt-in auxiliary scores file ─────────────────────────────────────────────────

    // Spec section 6 differences score_beta - score_0 over the beta arm's top 50; at parity beta ~35%
    // of those documents fall outside the beta=0 arm's own top 50, so a top-50-truncated file cannot
    // answer it. This file is untruncated, and full-precision rather than F6 -- F6's +/-1e-6 is the
    // same order as the differences being measured.
    [Fact]
    public async Task RunAsync_ScoresPath_WritesEveryDocumentAtFullPrecision_NotJustTheTopFifty()
    {
        var dir = TempDir("scores");
        try
        {
            var keyMap = new Dictionary<string, string>();
            var rows   = new List<(string, string, int, double)>();
            // 52 documents, one chunk each, descending -- two more than DocumentBudget.
            for (var i = 0; i < 52; i++)
            {
                keyMap[$"k{i}"] = $"doc{i:D2}";
                rows.Add(("q1", $"k{i}", i + 1, 0.9 - i * 0.001));
            }
            // A 53rd document whose score needs more than F6 to survive a round trip.
            keyMap["k-precise"] = "doc-precise";
            rows.Add(("q1", "k-precise", 53, 0.1234567890123));

            var keyMapPath = Path.Combine(dir, "keymap.json");
            await KeyMap.SaveAsync(keyMap, keyMapPath);

            var hitsPath = Path.Combine(dir, "pool.chunks.hits.tsv");
            await WriteHitsAsync(hitsPath, rows.ToArray());
            await WritePoolSidecarAsync(Path.Combine(dir, "pool.meta.json"), composite: "abc123");

            var scoresPath = Path.Combine(dir, "arm.scores.tsv");
            await new BenchmarkAggregateScenario().RunAsync(
                Flags(keyMapPath, hitsPath, dir, configLabel: "arm", scoresPath: scoresPath));

            var runLines = await File.ReadAllLinesAsync(Path.Combine(dir, "arm.chunks.trec"));
            runLines.Should().HaveCount(50, "the run file is still truncated to DocumentBudget");

            var scoreLines = await File.ReadAllLinesAsync(scoresPath);
            scoreLines[0].Should().Be("queryId\tdocId\tscore");
            scoreLines.Should().HaveCount(54, "header plus all 53 pooled documents, untruncated");
            scoreLines[1].Should().Be("q1\tdoc00\t0.9");
            scoreLines.Last().Should().Be("q1\tdoc-precise\t0.1234567890123",
                "F6 would round this to 0.123457 -- the noise floor spec section 6 cannot afford");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // The branch's central deliverable is a byte-identity on this file. A new opt-in output must not
    // perturb it in either direction.
    [Theory]
    // Every path this command reads or writes. `--scores-path` is opened with append:false, so aiming
    // it at any of these TRUNCATES that file. Pointing it at the dump destroyed a multi-hour pool
    // artefact and still exited 0 with a success-shaped sidecar, which is how it reached review.
    [InlineData("hits")]
    [InlineData("keymap")]
    [InlineData("runfile")]
    [InlineData("sidecar")]
    public async Task RunAsync_ScoresPathCollidingWithAnotherOfItsOwnFiles_RefusesBeforeWritingAnything(
        string collideWith)
    {
        var poolDir = TempDir("collide-pool");
        var outDir = TempDir("collide-out");
        try
        {
            var keyMapPath = Path.Combine(poolDir, "keymap.json");
            await KeyMap.SaveAsync(new Dictionary<string, string> { ["k1"] = "doc1" }, keyMapPath);

            var hitsPath = Path.Combine(poolDir, "pool.chunks.hits.tsv");
            await WriteHitsAsync(hitsPath, ("q1", "k1", 1, 0.9), ("q1", "k1", 2, 0.4));
            await WritePoolSidecarAsync(Path.Combine(poolDir, "pool.meta.json"), composite: "abc123");

            var hitsBefore = await File.ReadAllBytesAsync(hitsPath);
            var keyMapBefore = await File.ReadAllBytesAsync(keyMapPath);

            var scoresPath = collideWith switch
            {
                "hits" => hitsPath,
                "keymap" => keyMapPath,
                "runfile" => Path.Combine(outDir, "arm.chunks.trec"),
                "sidecar" => Path.Combine(outDir, "arm.meta.json"),
                _ => throw new ArgumentOutOfRangeException(nameof(collideWith)),
            };

            var act = async () => await new BenchmarkAggregateScenario().RunAsync(
                Flags(keyMapPath, hitsPath, outDir, configLabel: "arm", beta: 0.0358,
                      scoresPath: scoresPath));

            await act.Should().ThrowAsync<InvalidOperationException>();

            // Refused BEFORE any write: the inputs are untouched and the output directory is empty.
            // Without the guard the "hits" case rewrites the dump as a scores TSV and exits 0.
            (await File.ReadAllBytesAsync(hitsPath)).Should().Equal(hitsBefore);
            (await File.ReadAllBytesAsync(keyMapPath)).Should().Equal(keyMapBefore);
            Directory.GetFileSystemEntries(outDir).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(poolDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_ScoresPathIsADirectory_RefusesBeforeWritingAnything()
    {
        var poolDir = TempDir("scoresdir-pool");
        var outDir = TempDir("scoresdir-out");
        try
        {
            var keyMapPath = Path.Combine(poolDir, "keymap.json");
            await KeyMap.SaveAsync(new Dictionary<string, string> { ["k1"] = "doc1" }, keyMapPath);
            var hitsPath = Path.Combine(poolDir, "pool.chunks.hits.tsv");
            await WriteHitsAsync(hitsPath, ("q1", "k1", 1, 0.9), ("q1", "k1", 2, 0.4));
            await WritePoolSidecarAsync(Path.Combine(poolDir, "pool.meta.json"), composite: "abc123");

            // Previously this wrote the run file, then threw UnauthorizedAccessException on the scores
            // write -- leaving a run file with no sidecar to disown it, the exact shape the beta guard
            // exists to prevent.
            var act = async () => await new BenchmarkAggregateScenario().RunAsync(
                Flags(keyMapPath, hitsPath, outDir, configLabel: "arm", beta: 0.0358,
                      scoresPath: poolDir));

            await act.Should().ThrowAsync<InvalidOperationException>();
            Directory.GetFileSystemEntries(outDir).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(poolDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_ScoresPath_LeavesTheRunFileByteIdentical()
    {
        var poolDir = TempDir("identity-pool");
        var withOut = TempDir("identity-without");
        var withDir = TempDir("identity-with");
        try
        {
            var keyMapPath = Path.Combine(poolDir, "keymap.json");
            await KeyMap.SaveAsync(
                new Dictionary<string, string> { ["k1"] = "doc1", ["k2"] = "doc2" }, keyMapPath);

            var hitsPath = Path.Combine(poolDir, "pool.chunks.hits.tsv");
            await WriteHitsAsync(
                hitsPath,
                ("q1", "k1", 1, 0.9), ("q1", "k1", 2, 0.4), ("q1", "k2", 3, 0.85),
                ("q2", "k2", 1, 0.7), ("q2", "k1", 2, 0.65));
            await WritePoolSidecarAsync(Path.Combine(poolDir, "pool.meta.json"), composite: "abc123");

            await new BenchmarkAggregateScenario().RunAsync(
                Flags(keyMapPath, hitsPath, withOut, configLabel: "arm", beta: 0.0358));
            await new BenchmarkAggregateScenario().RunAsync(
                Flags(keyMapPath, hitsPath, withDir, configLabel: "arm", beta: 0.0358,
                      scoresPath: Path.Combine(withDir, "arm.scores.tsv")));

            var without = await File.ReadAllBytesAsync(Path.Combine(withOut, "arm.chunks.trec"));
            var with    = await File.ReadAllBytesAsync(Path.Combine(withDir, "arm.chunks.trec"));
            with.Should().Equal(without);
        }
        finally
        {
            Directory.Delete(poolDir, recursive: true);
            Directory.Delete(withOut, recursive: true);
            Directory.Delete(withDir, recursive: true);
        }
    }
}
