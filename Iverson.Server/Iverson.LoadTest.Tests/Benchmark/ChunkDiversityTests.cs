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
}
