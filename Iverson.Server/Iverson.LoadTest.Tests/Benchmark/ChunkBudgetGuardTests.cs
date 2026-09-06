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

    // Fix round 1: a missing "documents" or "chunks" key in the caller's JSON source binds to 0
    // via the default(int), not an exception. Before the double.IsFinite guard, a missing "chunks"
    // (documents present) made chunksPerDoc == 0.0, so reachable == topK / 0.0 == +Infinity, and
    // +Infinity >= documentBudget evaluated true -- Ok silently came back true, the inverse of the
    // guard's fail-closed contract. A missing "documents" happened to fail closed already (reachable
    // collapses to 0), so the two degenerate inputs behaved oppositely. These three cases assert Ok
    // is false for all of them, symmetrically.
    [Fact]
    public void Evaluate_ZeroChunksWithPositiveDocuments_FailsClosedNotOpen()
    {
        // Regression case for the bug: chunksPerDoc == 0.0 makes reachable == +Infinity, which
        // compares >= documentBudget as true unless finiteness is checked first.
        var result = ChunkBudgetGuard.Evaluate(documents: 6_000, chunks: 0, documentBudget: 50, chunkBudgetMultiplier: 5);

        result.Ok.Should().BeFalse();
        double.IsPositiveInfinity(result.ReachableDocuments).Should().BeTrue();
    }

    [Fact]
    public void Evaluate_ZeroDocumentsWithPositiveChunks_FailsClosed()
    {
        var result = ChunkBudgetGuard.Evaluate(documents: 0, chunks: 64_763, documentBudget: 50, chunkBudgetMultiplier: 5);

        result.Ok.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_ZeroDocumentsAndZeroChunks_FailsClosed()
    {
        // chunksPerDoc == 0.0 / 0.0 == NaN here; NaN >= documentBudget is already false without the
        // finiteness guard, but this asserts the "both missing" case stays refused too.
        var result = ChunkBudgetGuard.Evaluate(documents: 0, chunks: 0, documentBudget: 50, chunkBudgetMultiplier: 5);

        result.Ok.Should().BeFalse();
    }
}
