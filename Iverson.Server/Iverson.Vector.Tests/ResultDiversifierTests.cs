using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iverson.Vector.Tests;

public sealed class ResultDiversifierTests
{
    private readonly ResultDiversifier _diversifier = new(Options.Create(new VectorRankingOptions()));

    [Fact]
    public void Diversify_AllVectorsPresent_LambdaArithmeticDrivesSelectionOrder()
    {
        // A selected first (unconditional). Then MMR(B) = 0.7*0.9 - 0.3*1.0 = 0.33
        // (identical vector to A -> similarity 1.0), MMR(C) = 0.7*0.8 - 0.3*0.0 = 0.56
        // (orthogonal to A -> similarity 0.0). C beats B, so C is picked before B.
        var candidates = new[]
        {
            new DiversifyCandidate(1, Score: 1.0, DiversityVector: new float[] { 1f, 0f }),
            new DiversifyCandidate(2, Score: 0.9, DiversityVector: new float[] { 1f, 0f }),
            new DiversifyCandidate(3, Score: 0.8, DiversityVector: new float[] { 0f, 1f })
        };

        var results = _diversifier.Diversify(candidates, topK: 3);

        results.Select(r => r.Id).Should().ContainInOrder(1UL, 3UL, 2UL);
        results.Select(r => r.FusedScore).Should().Equal(1.0, 0.8, 0.9);
    }

    [Fact]
    public void Diversify_DissimilarLowerFusedCandidate_PromotedOverHigherFusedNearDuplicate()
    {
        // A selected first. MMR(D) = 0.7*0.95 - 0.3*1.0 = 0.365 (near-duplicate of A).
        // MMR(E) = 0.7*0.60 - 0.3*0.0 = 0.42 (dissimilar to A). E wins despite a
        // strictly lower fused score than D (0.60 < 0.95).
        var candidates = new[]
        {
            new DiversifyCandidate(1, Score: 1.00, DiversityVector: new float[] { 1f, 0f }),
            new DiversifyCandidate(2, Score: 0.95, DiversityVector: new float[] { 1f, 0f }),
            new DiversifyCandidate(3, Score: 0.60, DiversityVector: new float[] { 0f, 1f })
        };

        var results = _diversifier.Diversify(candidates, topK: 2);

        results.Select(r => r.Id).Should().ContainInOrder(1UL, 3UL);
    }

    [Fact]
    public void Diversify_HighestFusedCandidate_AlwaysSelectedFirst_EvenWhenMostRedundant()
    {
        // All three vectors are identical, so every candidate is maximally redundant with
        // every other one. The first pick must still be the highest-fused candidate
        // unconditionally, and remaining ties break toward the earlier (fused-descending) index.
        var vector = new float[] { 1f, 0f };
        var candidates = new[]
        {
            new DiversifyCandidate(1, Score: 1.0, DiversityVector: vector),
            new DiversifyCandidate(2, Score: 0.9, DiversityVector: vector),
            new DiversifyCandidate(3, Score: 0.8, DiversityVector: vector)
        };

        var results = _diversifier.Diversify(candidates, topK: 3);

        results.Select(r => r.Id).Should().ContainInOrder(1UL, 2UL, 3UL);
    }

    [Fact]
    public void Diversify_EveryDiversityVectorAbsent_MatchesTakeTopKBitForBit()
    {
        var candidates = new[]
        {
            new DiversifyCandidate(1, Score: 10.0, DiversityVector: null),
            new DiversifyCandidate(2, Score: 8.0, DiversityVector: null),
            new DiversifyCandidate(3, Score: 8.0, DiversityVector: null),
            new DiversifyCandidate(4, Score: 3.0, DiversityVector: null),
            new DiversifyCandidate(5, Score: 1.0, DiversityVector: null)
        };

        var results = _diversifier.Diversify(candidates, topK: 3);

        var expected = candidates.Take(3).Select(c => new RerankedResult(c.Id, c.Score));
        results.Should().Equal(expected);
    }

    [Fact]
    public void Diversify_OneVectorAbsent_ThatPairContributesNoPenalty()
    {
        // A selected first. B is a near-duplicate of A and takes the full penalty:
        // MMR(B) = 0.7*0.90 - 0.3*1.0 = 0.33. C's vector is absent, so it takes no
        // penalty at all: MMR(C) = 0.7*0.85 = 0.595. C wins despite a lower fused score.
        var candidates = new[]
        {
            new DiversifyCandidate(1, Score: 1.00, DiversityVector: new float[] { 1f, 0f }),
            new DiversifyCandidate(2, Score: 0.90, DiversityVector: new float[] { 1f, 0f }),
            new DiversifyCandidate(3, Score: 0.85, DiversityVector: null)
        };

        var results = _diversifier.Diversify(candidates, topK: 2);

        results.Select(r => r.Id).Should().ContainInOrder(1UL, 3UL);
    }

    [Fact]
    public void Diversify_AntiSimilarCandidate_OutranksZeroSimilarityCandidate_AtEqualFusedScore()
    {
        // A selected first. P is anti-similar to A (cosine = -1), so its penalty term is
        // NEGATIVE -- a bonus: MMR(P) = 0.7*0.5 - 0.3*(-1.0) = 0.65. Z is orthogonal to A
        // (cosine = 0): MMR(Z) = 0.7*0.5 - 0.3*0.0 = 0.35. Equal fused scores, but P wins.
        // A running-maximum that incorrectly clamped similarity at 0 would instead compute
        // MMR(P) = 0.35, tying Z instead of beating it.
        var candidates = new[]
        {
            new DiversifyCandidate(1, Score: 1.0, DiversityVector: new float[] { 1f, 0f }),
            new DiversifyCandidate(2, Score: 0.5, DiversityVector: new float[] { -1f, 0f }),
            new DiversifyCandidate(3, Score: 0.5, DiversityVector: new float[] { 0f, 1f })
        };

        var results = _diversifier.Diversify(candidates, topK: 2);

        results.Select(r => r.Id).Should().ContainInOrder(1UL, 2UL);
    }

    [Fact]
    public void Diversify_NaNFusedScoreInPool_ReturnsExactlyMinTopKAndPoolCount()
    {
        // NaN placed LAST: Diversify's precondition is a fused-descending pool, and Rerank's
        // OrderByDescending(r => r.FusedScore) uses Comparer<double>.Default, which sorts NaN
        // as smaller than every real value -- so a NaN candidate is always at the tail of any
        // pool production actually hands to Diversify.
        var candidates = new[]
        {
            new DiversifyCandidate(1, Score: 1.0, DiversityVector: null),
            new DiversifyCandidate(3, Score: 0.5, DiversityVector: null),
            new DiversifyCandidate(2, Score: double.NaN, DiversityVector: null)
        };

        var results = _diversifier.Diversify(candidates, topK: 3);

        results.Select(r => r.Id).Should().Equal(1UL, 3UL, 2UL);
    }

    [Fact]
    public void Diversify_DifferingVectorLengths_TreatedAsAbsent_DoesNotThrow()
    {
        // B's vector has a different length than A's. That pair must be skipped before the
        // cosine-similarity call (which throws ArgumentException on length mismatch), so B
        // takes no penalty: MMR(B) = 0.7*0.9 = 0.63. C has no vector at all: MMR(C) = 0.7*0.5
        // = 0.35. B wins.
        var candidates = new[]
        {
            new DiversifyCandidate(1, Score: 1.0, DiversityVector: new float[] { 1f, 0f }),
            new DiversifyCandidate(2, Score: 0.9, DiversityVector: new float[] { 1f, 0f, 0f }),
            new DiversifyCandidate(3, Score: 0.5, DiversityVector: null)
        };

        Func<IReadOnlyList<RerankedResult>> act = () => _diversifier.Diversify(candidates, topK: 2);

        var results = act.Should().NotThrow().Subject;
        results.Select(r => r.Id).Should().ContainInOrder(1UL, 2UL);
    }

    [Fact]
    public void Diversify_ZeroMagnitudeVector_NaNTreatedAsAbsent_DoesNotWronglyWinOverStrictlyBetterCandidate()
    {
        // A selected first. Z has a zero-magnitude vector: cosine similarity against A is
        // NaN and must be treated as ABSENT (not stored), so MMR(Z) = 0.7*0.5 = 0.35.
        // W is anti-similar to A (cosine = -1), earning a real bonus:
        // MMR(W) = 0.7*0.4 - 0.3*(-1.0) = 0.58. W is strictly better and must win even
        // though Z appears earlier in the candidate list. A buggy implementation that let
        // the NaN similarity survive into the MMR formula (rather than skipping it) would
        // produce MMR(Z) = NaN, which loses every '>' comparison -- including against W's
        // real 0.58 -- and would incorrectly leave Z stuck as the running best.
        var candidates = new[]
        {
            new DiversifyCandidate(1, Score: 1.0, DiversityVector: new float[] { 1f, 0f }),
            new DiversifyCandidate(2, Score: 0.5, DiversityVector: new float[] { 0f, 0f }),
            new DiversifyCandidate(3, Score: 0.4, DiversityVector: new float[] { -1f, 0f })
        };

        var results = _diversifier.Diversify(candidates, topK: 2);

        results.Select(r => r.Id).Should().ContainInOrder(1UL, 3UL);
    }

    [Fact]
    public void Diversify_PoolSmallerThanTopK_ReturnsEveryCandidate_OrderedByMmr()
    {
        // Only 3 candidates but topK=5. All 3 must be returned, and their order is
        // MMR-determined rather than fused-descending: A first (unconditional), then C
        // (MMR 0.49) ahead of B (MMR 0.33), even though B's fused score (0.9) beats C's (0.7).
        var candidates = new[]
        {
            new DiversifyCandidate(1, Score: 1.0, DiversityVector: new float[] { 1f, 0f }),
            new DiversifyCandidate(2, Score: 0.9, DiversityVector: new float[] { 1f, 0f }),
            new DiversifyCandidate(3, Score: 0.7, DiversityVector: new float[] { 0f, 1f })
        };

        var results = _diversifier.Diversify(candidates, topK: 5);

        results.Should().HaveCount(3);
        results.Select(r => r.Id).Should().ContainInOrder(1UL, 3UL, 2UL);
    }

    [Fact]
    public void Diversify_EmptyPool_ReturnsEmptyResult()
    {
        var results = _diversifier.Diversify(Array.Empty<DiversifyCandidate>(), topK: 3);

        results.Should().BeEmpty();
    }

    [Fact]
    public void Diversify_TopKOfOne_ReturnsOnlyHighestFusedCandidate()
    {
        var candidates = new[]
        {
            new DiversifyCandidate(1, Score: 1.0, DiversityVector: new float[] { 1f, 0f }),
            new DiversifyCandidate(2, Score: 0.5, DiversityVector: new float[] { 0f, 1f })
        };

        var results = _diversifier.Diversify(candidates, topK: 1);

        results.Should().Equal(new RerankedResult(1, 1.0));
    }
}
