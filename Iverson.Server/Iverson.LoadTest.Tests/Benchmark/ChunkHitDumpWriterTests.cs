using FluentAssertions;
using Iverson.LoadTest.Benchmark;
using Xunit;

namespace Iverson.LoadTest.Tests.Benchmark;

public class ChunkHitDumpWriterTests
{
    private static string TempPath(string suffix) =>
        Path.Combine(Path.GetTempPath(), $"chunk-hit-dump-test-{Guid.NewGuid()}-{suffix}");

    [Fact]
    public async Task WriteAsync_NoResults_YieldsHeaderOnlyFile()
    {
        var path = TempPath("no-results.tsv");
        try
        {
            await ChunkHitDumpWriter.WriteAsync(
                path, Array.Empty<(string, IReadOnlyList<(string, double)>)>());

            var lines = await File.ReadAllLinesAsync(path);
            lines.Should().Equal("queryId\tparentKey\trank\tscore");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task WriteAsync_QueryWithNoHits_YieldsHeaderOnlyFile()
    {
        var path = TempPath("empty-hits.tsv");
        try
        {
            var results = new List<(string QueryId, IReadOnlyList<(string ParentKey, double Score)> Hits)>
            {
                ("q1", Array.Empty<(string, double)>()),
            };

            await ChunkHitDumpWriter.WriteAsync(path, results);

            var lines = await File.ReadAllLinesAsync(path);
            lines.Should().Equal("queryId\tparentKey\trank\tscore");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task WriteAsync_RanksAreOneBasedAndResetPerQuery()
    {
        var path = TempPath("ranks.tsv");
        try
        {
            var results = new List<(string QueryId, IReadOnlyList<(string ParentKey, double Score)> Hits)>
            {
                ("q1", new (string, double)[] { ("p1", 0.9), ("p2", 0.5), ("p3", 0.1) }),
                ("q2", new (string, double)[] { ("p4", 0.7) }),
            };

            await ChunkHitDumpWriter.WriteAsync(path, results);

            var lines = await File.ReadAllLinesAsync(path);
            lines.Should().Equal(
                "queryId\tparentKey\trank\tscore",
                "q1\tp1\t1\t0.9",
                "q1\tp2\t2\t0.5",
                "q1\tp3\t3\t0.1",
                "q2\tp4\t1\t0.7");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task WriteAsync_PreservesSuppliedOrder_DoesNotSort()
    {
        var path = TempPath("order.tsv");
        try
        {
            // Deliberately NOT score-descending -- the writer must not reorder rows; row order is
            // load-bearing (Global Constraint: a later task byte-compares a run rebuilt from this
            // dump against the original).
            var results = new List<(string QueryId, IReadOnlyList<(string ParentKey, double Score)> Hits)>
            {
                ("q1", new (string, double)[] { ("low", 0.1), ("high", 0.9), ("mid", 0.5) }),
            };

            await ChunkHitDumpWriter.WriteAsync(path, results);

            var lines = await File.ReadAllLinesAsync(path);
            lines.Should().Equal(
                "queryId\tparentKey\trank\tscore",
                "q1\tlow\t1\t0.1",
                "q1\thigh\t2\t0.9",
                "q1\tmid\t3\t0.5");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task WriteAsync_ScoresAreRoundTripFormatted_NotF6DisplayFormatted()
    {
        var path = TempPath("roundtrip.tsv");
        try
        {
            // Score originates as a widened float32 (spec: r.Score off the gRPC wire is a float),
            // so its double representation carries the float32's trailing imprecision.
            // (double)0.1f != 0.1 exactly, and F6 would silently round that away to "0.100000",
            // losing the distinction a later beta-replay task depends on.
            double widenedFloat = (float)0.1f;
            var results = new List<(string QueryId, IReadOnlyList<(string ParentKey, double Score)> Hits)>
            {
                ("q1", new (string, double)[] { ("p1", widenedFloat) }),
            };

            await ChunkHitDumpWriter.WriteAsync(path, results);

            var lines = await File.ReadAllLinesAsync(path);
            lines.Should().HaveCount(2);
            lines[1].Should().Be($"q1\tp1\t1\t{widenedFloat.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

            var f6Formatted = widenedFloat.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
            lines[1].Should().NotEndWith(
                $"\t{f6Formatted}", "F6 display formatting must not be used for this dump");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task WriteAsync_DistinctNearTiedScores_StayDistinctInOutput()
    {
        var path = TempPath("near-tied.tsv");
        try
        {
            // These two scores are distinct doubles that F6 rounds to the identical 6-decimal
            // string ("0.123456") -- exactly the collapse the brief forbids. Round-trip formatting
            // must keep them distinguishable.
            const double a = 0.123456;
            const double b = 0.1234561;

            var results = new List<(string QueryId, IReadOnlyList<(string ParentKey, double Score)> Hits)>
            {
                ("q1", new (string, double)[] { ("p1", a), ("p2", b) }),
            };

            await ChunkHitDumpWriter.WriteAsync(path, results);

            var lines = await File.ReadAllLinesAsync(path);
            var scoreColumns = lines.Skip(1).Select(l => l.Split('\t')[3]).ToList();
            scoreColumns.Should().OnlyHaveUniqueItems();
            scoreColumns.Should().Equal(
                a.ToString(System.Globalization.CultureInfo.InvariantCulture),
                b.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task WriteAsync_CreatesMissingParentDirectory()
    {
        var dir  = Path.Combine(Path.GetTempPath(), $"chunk-hit-dump-test-dir-{Guid.NewGuid()}");
        var path = Path.Combine(dir, "run.chunks.hits.tsv");
        try
        {
            var results = new List<(string QueryId, IReadOnlyList<(string ParentKey, double Score)> Hits)>
            {
                ("q1", new (string, double)[] { ("p1", 0.5) }),
            };

            await ChunkHitDumpWriter.WriteAsync(path, results);

            File.Exists(path).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
