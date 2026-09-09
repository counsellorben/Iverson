using FluentAssertions;
using Iverson.LoadTest.Benchmark;
using Xunit;

namespace Iverson.LoadTest.Tests.Benchmark;

public class DocumentRankingTests
{
    [Fact]
    public void CollapseByDocId_SameDocIdTwice_KeepsTheMaximumScore()
    {
        // This is the SearchSimilar defect in miniature: two entities, one DocId. Emitting both puts
        // the same doc id at two ranks in the run file, which TREC scorers reject or silently
        // collapse — either way the ranking scored is not the ranking produced.
        var scored = new[] { ("doc-1", 0.4), ("doc-1", 0.9) };

        var result = DocumentRanking.CollapseByDocId(scored, limit: 10);

        result.Should().ContainSingle().Which.Should().Be(("doc-1", 0.9));
    }

    [Fact]
    public void CollapseByDocId_KeepsMaximumRegardlessOfInputOrder()
    {
        // Guards against "first one wins": the higher score arrives first here, last in the test above.
        var scored = new[] { ("doc-1", 0.9), ("doc-1", 0.4) };

        var result = DocumentRanking.CollapseByDocId(scored, limit: 10);

        result.Should().ContainSingle().Which.Should().Be(("doc-1", 0.9));
    }

    [Fact]
    public void CollapseByDocId_OrdersByScoreDescending()
    {
        var scored = new[] { ("doc-low", 0.1), ("doc-high", 0.9), ("doc-mid", 0.5) };

        var result = DocumentRanking.CollapseByDocId(scored, limit: 10);

        result.Should().BeEquivalentTo(new[]
        {
            ("doc-high", 0.9),
            ("doc-mid", 0.5),
            ("doc-low", 0.1),
        }, options => options.WithStrictOrdering());
    }

    [Fact]
    public void CollapseByDocId_TruncatesToLimitAfterCollapsing_NotBefore()
    {
        // Truncating first would return two rows for one document and drop a real one. Three inputs,
        // two distinct documents, limit 2: the answer is both documents, not doc-1 twice.
        var scored = new[] { ("doc-1", 0.9), ("doc-1", 0.8), ("doc-2", 0.7) };

        var result = DocumentRanking.CollapseByDocId(scored, limit: 2);

        result.Should().BeEquivalentTo(new[]
        {
            ("doc-1", 0.9),
            ("doc-2", 0.7),
        }, options => options.WithStrictOrdering());
    }

    [Fact]
    public void CollapseByDocId_MoreDocumentsThanLimit_TruncatesToExactlyLimit()
    {
        var scored = Enumerable.Range(0, 10).Select(i => ($"doc-{i}", (double)i)).ToList();

        var result = DocumentRanking.CollapseByDocId(scored, limit: 3);

        result.Should().HaveCount(3);
        result[0].Should().Be(("doc-9", 9.0));
    }

    [Fact]
    public void CollapseByDocId_NoInput_ReturnsEmpty()
    {
        DocumentRanking.CollapseByDocId(Array.Empty<(string DocId, double Score)>(), limit: 10).Should().BeEmpty();
    }

    [Fact]
    public void CollapseByDocId_WithText_KeepsTheTextOfTheMaximumChunk()
    {
        var scored = new[] { ("d1", 0.2, "low"), ("d1", 0.9, "winner"), ("d1", 0.5, "mid"), ("d2", 0.7, "only") };

        var result = DocumentRanking.CollapseByDocId(scored, limit: 10);

        result.Should().Equal(("d1", 0.9, "winner"), ("d2", 0.7, "only"));
    }

    [Fact]
    public void CollapseByDocId_WithText_TieKeepsTheFirstSeenChunk()
    {
        // Same rule as the 2-tuple overload (`score > existing`): on an exact tie the first chunk wins.
        var scored = new[] { ("d1", 0.5, "first"), ("d1", 0.5, "second") };

        DocumentRanking.CollapseByDocId(scored, limit: 10).Should().Equal(("d1", 0.5, "first"));
    }

    [Fact]
    public void CollapseByDocIdWithTail_FiveChunks_SumsOnlyThe2ndThrough4thHighest()
    {
        // Tail is capped at 3: the 5th-highest chunk (0.1) must not contribute.
        var scored = new[]
        {
            ("doc-1", 0.9),
            ("doc-1", 0.5),
            ("doc-1", 0.4),
            ("doc-1", 0.3),
            ("doc-1", 0.1),
        };

        var result = DocumentRanking.CollapseByDocIdWithTail(scored, limit: 10, beta: 1.0);

        result.Should().ContainSingle().Which.Should().Be(("doc-1", 0.9 + (0.5 + 0.4 + 0.3)));
    }

    [Fact]
    public void CollapseByDocIdWithTail_TwoChunks_SumsOnlyTheSecond()
    {
        var scored = new[] { ("doc-1", 0.9), ("doc-1", 0.4) };

        var result = DocumentRanking.CollapseByDocIdWithTail(scored, limit: 10, beta: 1.0);

        result.Should().ContainSingle().Which.Should().Be(("doc-1", 0.9 + 0.4));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void CollapseByDocIdWithTail_SingleChunkDocument_ScoresExactlyItsMaxAtEveryBeta(double beta)
    {
        var scored = new[] { ("doc-1", 0.7) };

        var result = DocumentRanking.CollapseByDocIdWithTail(scored, limit: 10, beta);

        result.Should().ContainSingle().Which.Should().Be(("doc-1", 0.7));
    }

    [Fact]
    public void CollapseByDocIdWithTail_OrdersByAugmentedScore_NotByMax()
    {
        // doc-low has the higher max (0.9) but no tail; doc-high has a lower max (0.6) plus a
        // strong tail. At beta 1.0 the augmented score must flip the ordering.
        var scored = new[]
        {
            ("doc-low", 0.9),
            ("doc-high", 0.6),
            ("doc-high", 0.5),
            ("doc-high", 0.5),
        };

        var result = DocumentRanking.CollapseByDocIdWithTail(scored, limit: 10, beta: 1.0);

        result.Should().BeEquivalentTo(new[]
        {
            ("doc-high", 0.6 + 0.5 + 0.5),
            ("doc-low", 0.9),
        }, options => options.WithStrictOrdering());
    }

    [Fact]
    public void CollapseByDocIdWithTail_TruncatesToLimitAfterCollapsing_NotBefore()
    {
        var scored = new[] { ("doc-1", 0.9), ("doc-1", 0.8), ("doc-2", 0.7) };

        var result = DocumentRanking.CollapseByDocIdWithTail(scored, limit: 2, beta: 1.0);

        result.Should().HaveCount(2);
        result.Select(r => r.DocId).Should().BeEquivalentTo(new[] { "doc-1", "doc-2" });
    }

    [Fact]
    public void CollapseByDocIdWithTail_BetaZero_EqualsCollapseByDocId()
    {
        var scored = new[]
        {
            ("doc-1", 0.9),
            ("doc-1", 0.5),
            ("doc-1", 0.4),
            ("doc-2", 0.7),
            ("doc-2", 0.2),
        };

        var tailResult = DocumentRanking.CollapseByDocIdWithTail(scored, limit: 10, beta: 0);
        var maxResult  = DocumentRanking.CollapseByDocId(scored, limit: 10);

        tailResult.Should().Equal(maxResult);
    }
}
