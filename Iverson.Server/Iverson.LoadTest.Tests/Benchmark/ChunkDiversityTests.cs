using FluentAssertions;
using Iverson.LoadTest.Benchmark;
using Xunit;

namespace Iverson.LoadTest.Tests.Benchmark;

public class ChunkDiversityTests
{
    private static IReadOnlyList<string> Parents(params string[] p) => p;

    [Fact]
    public void Count_DistinctParentsWithinEachPrefix_NotAcrossTheWholeList()
    {
        // 12 hits: parents a,a,b,a,c,c,d,e,e,f | g,h  -> top10 has 6 distinct, top50 has 8
        var hits = Parents("a", "a", "b", "a", "c", "c", "d", "e", "e", "f", "g", "h");
        var (at10, at50) = ChunkDiversity.Count(hits);
        at10.Should().Be(6);
        at50.Should().Be(8);
    }

    [Fact]
    public void Count_FewerHitsThanTheWindow_CountsWhatIsThere()
    {
        var (at10, at50) = ChunkDiversity.Count(Parents("a", "b", "a"));
        at10.Should().Be(2);
        at50.Should().Be(2);
    }

    [Fact]
    public void Count_Empty_IsZero() => ChunkDiversity.Count(Array.Empty<string>()).Should().Be((0, 0));

    [Fact]
    public void Count_MoreThanFiftyHits_At50IgnoresParentsBeyondTheWindow()
    {
        // 50 hits cycling through 5 parents (p0..p4 -> distinct-at-50 == 5), followed by 10
        // MORE hits introducing 10 brand-new parents beyond the window. Deleting `.Take(50)`
        // from ChunkDiversity.Count would fold those 10 new parents into the total, making
        // At50 == 15 instead of 5 -- exactly the defect this fixture exists to catch. The
        // largest prior fixture (12 hits) could never fail that mutation: Take(50) is a
        // no-op below 50 hits, so it passed with .Take(50) deleted entirely.
        var window = Enumerable.Range(0, 50).Select(i => $"p{i % 5}");
        var beyond = Enumerable.Range(0, 10).Select(i => $"q{i}");
        var hits = window.Concat(beyond).ToArray();

        var (at10, at50) = ChunkDiversity.Count(hits);

        at10.Should().Be(5);   // first 10 hits: p0..p4, each exactly once
        at50.Should().Be(5);   // first 50 hits: same 5 parents repeated 10x -- the 10 new
                                // parents beyond the window must not count
    }
}
