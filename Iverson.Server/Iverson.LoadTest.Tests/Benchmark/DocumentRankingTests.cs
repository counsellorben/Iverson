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
        DocumentRanking.CollapseByDocId([], limit: 10).Should().BeEmpty();
    }
}
