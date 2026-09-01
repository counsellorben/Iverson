using FluentAssertions;
using Iverson.LoadTest.Benchmark;
using Xunit;

namespace Iverson.LoadTest.Tests.Benchmark;

/// <summary>
/// Table-driven over the real corpus densities recorded on disk, so each case states the
/// consequence the spec accepted rather than an invented one.
/// </summary>
public class ChunkBudgetGuardTests
{
    [Theory]
    [InlineData(6_000, 64_763, 5, false, 11)] // 10.79 chunks/doc -- FreshStack
    [InlineData(6_000, 33_950, 5, false, 6)]  // 5.66 chunks/doc
    [InlineData(6_000, 18_622, 5, true, null)] // 3.10 chunks/doc
    [InlineData(8_674, 24_282, 5, true, null)] // 2.80 chunks/doc
    [InlineData(6_000, 64_763, 20, true, null)] // same corpus as row 1, raised multiplier
    public void Evaluate_RealCorpusDensities_MatchesAcceptedConsequence(
        int documents, int chunks, int chunkBudgetMultiplier, bool expectedOk, int? expectedMinimumMultiplier)
    {
        var result = ChunkBudgetGuard.Evaluate(documents, chunks, documentBudget: 50, chunkBudgetMultiplier);

        result.Ok.Should().Be(expectedOk);
        if (expectedMinimumMultiplier is { } expected)
            result.MinimumMultiplier.Should().Be(expected);
    }

    [Fact]
    public void Evaluate_ComputesChunksPerDocument()
    {
        var result = ChunkBudgetGuard.Evaluate(documents: 6_000, chunks: 64_763, documentBudget: 50, chunkBudgetMultiplier: 5);

        result.ChunksPerDocument.Should().BeApproximately(10.79, 0.01);
    }

    [Fact]
    public void Evaluate_ComputesChunkTopKAsDocumentBudgetTimesMultiplier()
    {
        var result = ChunkBudgetGuard.Evaluate(documents: 6_000, chunks: 18_622, documentBudget: 50, chunkBudgetMultiplier: 5);

        result.ChunkTopK.Should().Be(250);
    }
}
