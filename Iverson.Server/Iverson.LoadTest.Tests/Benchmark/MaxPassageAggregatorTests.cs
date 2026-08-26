using FluentAssertions;
using Iverson.LoadTest.Benchmark;
using Xunit;

namespace Iverson.LoadTest.Tests.Benchmark;

public class MaxPassageAggregatorTests
{
    [Fact]
    public void Aggregate_SeveralChunksOfOneParent_CollapseToMaxScore()
    {
        var chunks = new[]
        {
            ("key-1", 0.2),
            ("key-1", 0.9),
            ("key-1", 0.5),
        };
        var keyMap = new Dictionary<string, string> { ["key-1"] = "doc-1" };

        var result = MaxPassageAggregator.Aggregate(chunks, keyMap, limit: 10);

        result.Ranked.Should().ContainSingle().Which.Should().Be(("doc-1", 0.9));
        result.UnresolvedParentKeys.Should().BeEmpty();
    }

    [Fact]
    public void Aggregate_OrdersByAggregatedScoreDescending_NotInputOrder()
    {
        var chunks = new[]
        {
            ("key-1", 0.1),
            ("key-2", 0.9),
            ("key-3", 0.5),
        };
        var keyMap = new Dictionary<string, string>
        {
            ["key-1"] = "doc-low",
            ["key-2"] = "doc-high",
            ["key-3"] = "doc-mid",
        };

        var result = MaxPassageAggregator.Aggregate(chunks, keyMap, limit: 10);

        result.Ranked.Should().BeEquivalentTo(new[]
        {
            ("doc-high", 0.9),
            ("doc-mid", 0.5),
            ("doc-low", 0.1),
        }, options => options.WithStrictOrdering());
    }

    [Fact]
    public void Aggregate_MoreParentsThanLimit_TruncatesToExactlyLimit()
    {
        var chunks = Enumerable.Range(0, 10)
            .Select(i => ($"key-{i}", (double)i))
            .ToList();
        var keyMap = Enumerable.Range(0, 10)
            .ToDictionary(i => $"key-{i}", i => $"doc-{i}");

        var result = MaxPassageAggregator.Aggregate(chunks, keyMap, limit: 3);

        result.Ranked.Should().HaveCount(3);
        result.Ranked.Should().BeEquivalentTo(new[]
        {
            ("doc-9", 9.0),
            ("doc-8", 8.0),
            ("doc-7", 7.0),
        }, options => options.WithStrictOrdering());
    }

    [Fact]
    public void Aggregate_ParentKeyMissingFromKeyMap_IsReportedNotThrown()
    {
        var chunks = new[] { ("unknown-key", 0.5) };
        var keyMap = new Dictionary<string, string>();

        var result = MaxPassageAggregator.Aggregate(chunks, keyMap, limit: 10);

        // Reported rather than thrown so the caller can survey every query before failing once;
        // BenchmarkQueryScenario is what turns a non-empty UnresolvedParentKeys into a failed run.
        result.UnresolvedParentKeys.Should().ContainSingle().Which.Should().Be("unknown-key");
        result.Ranked.Should().BeEmpty();
    }

    [Fact]
    public void Aggregate_UnresolvedParent_IsExcludedFromTheRankingItWouldOtherwiseTop()
    {
        // The unresolved chunk outscores the resolvable one. If it leaked into the ranking it would
        // sit at rank 1 under some doc id; it must be absent AND reported.
        var chunks = new[] { ("unknown-key", 0.99), ("key-1", 0.10) };
        var keyMap = new Dictionary<string, string> { ["key-1"] = "doc-1" };

        var result = MaxPassageAggregator.Aggregate(chunks, keyMap, limit: 10);

        result.Ranked.Should().ContainSingle().Which.Should().Be(("doc-1", 0.10));
        result.UnresolvedParentKeys.Should().ContainSingle().Which.Should().Be("unknown-key");
    }

    [Fact]
    public void Aggregate_TwoParentsOneDocId_CollapseToMaxScore()
    {
        // The same corpus ingested twice: distinct parent keys, one doc id. A TREC run listing that
        // doc id at two ranks is malformed, so the aggregation must collapse them.
        var chunks = new[] { ("key-a", 0.3), ("key-b", 0.8) };
        var keyMap = new Dictionary<string, string> { ["key-a"] = "doc-1", ["key-b"] = "doc-1" };

        var result = MaxPassageAggregator.Aggregate(chunks, keyMap, limit: 10);

        result.Ranked.Should().ContainSingle().Which.Should().Be(("doc-1", 0.8));
    }
}
