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

        result.Should().ContainSingle().Which.Should().Be(("doc-1", 0.9));
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

        result.Should().BeEquivalentTo(new[]
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

        result.Should().HaveCount(3);
        result.Should().BeEquivalentTo(new[]
        {
            ("doc-9", 9.0),
            ("doc-8", 8.0),
            ("doc-7", 7.0),
        }, options => options.WithStrictOrdering());
    }

    [Fact]
    public void Aggregate_ParentKeyMissingFromKeyMap_ThrowsNamingTheKey()
    {
        var chunks = new[] { ("unknown-key", 0.5) };
        var keyMap = new Dictionary<string, string>();

        var act = () => MaxPassageAggregator.Aggregate(chunks, keyMap, limit: 10);

        act.Should().Throw<InvalidOperationException>().WithMessage("*unknown-key*");
    }
}
