using FluentAssertions;
using Xunit;

namespace Iverson.Vector.Tests;

public sealed class ResultRerankerTests
{
    private static readonly float[] Query = { 1f, 0f };

    // Centroid at 60 degrees from Query: cos(60 deg) = 0.5 exactly, both unit length.
    private static readonly float[] CentroidCos0Point5 = { 0.5f, 0.8660254f };

    // Centroid aligned with Query: cos = 1.0 exactly regardless of magnitude.
    private static readonly float[] CentroidCos1 = { 1f, 0f };

    private readonly ResultReranker _reranker = new();

    [Fact]
    public void Rerank_AllThreeSignalsPresent_ComputesWeightedMean()
    {
        var candidates = new[]
        {
            new RerankCandidate(1, BaseScore: 0.9, Centroid: CentroidCos0Point5, Decay: 0.8)
        };

        var results = _reranker.Rerank(Query, candidates);

        // (0.45*0.9 + 0.45*0.5 + 0.1*0.8) / 1.0 = 0.71
        results.Single().FusedScore.Should().BeApproximately(0.71, 1e-6);
    }

    [Fact]
    public void Rerank_CentroidSignal_PromotesCandidateAboveHigherBaseScore()
    {
        var candidates = new[]
        {
            new RerankCandidate(1, BaseScore: 0.5, Centroid: null, Decay: null),
            new RerankCandidate(2, BaseScore: 0.4, Centroid: CentroidCos1, Decay: null)
        };

        var results = _reranker.Rerank(Query, candidates);

        // Candidate 1: fused = 0.5 (base only).
        // Candidate 2: fused = (0.45*0.4 + 0.45*1.0) / 0.9 = 0.7.
        results[0].Id.Should().Be(2);
        results[0].FusedScore.Should().BeApproximately(0.7, 1e-6);
        results[1].Id.Should().Be(1);
        results[1].FusedScore.Should().BeApproximately(0.5, 1e-6);
    }

    [Fact]
    public void Rerank_DecaySignal_BreaksTieBetweenEqualBaseAndCentroid()
    {
        var candidates = new[]
        {
            new RerankCandidate(1, BaseScore: 0.5, Centroid: CentroidCos0Point5, Decay: 1.0),
            new RerankCandidate(2, BaseScore: 0.5, Centroid: CentroidCos0Point5, Decay: 0.0)
        };

        var results = _reranker.Rerank(Query, candidates);

        // Both: 0.45*0.5 + 0.45*0.5 = 0.45 before decay (unchanged: base and centroid are equal here).
        // Candidate 1: 0.45 + 0.1*1.0 = 0.55. Candidate 2: 0.45 + 0.1*0.0 = 0.45.
        results[0].Id.Should().Be(1);
        results[0].FusedScore.Should().BeApproximately(0.55, 1e-6);
        results[1].Id.Should().Be(2);
        results[1].FusedScore.Should().BeApproximately(0.45, 1e-6);
    }

    [Fact]
    public void Rerank_CentroidAbsent_RenormalizesOverBaseAndDecayOnly()
    {
        var candidates = new[]
        {
            new RerankCandidate(1, BaseScore: 0.8, Centroid: null, Decay: 0.6)
        };

        var results = _reranker.Rerank(Query, candidates);

        // weightedSum = 0.36 + 0.06 = 0.42; weightTotal = 0.55; fused = 0.42/0.55.
        // Decay's share is 0.1/0.55 = 18.18% here, against 10.00% when the centroid is present.
        var expected = (0.45 * 0.8 + 0.1 * 0.6) / 0.55;
        expected.Should().BeApproximately(0.818181818 * 0.8 + 0.181818182 * 0.6, 1e-6);
        results.Single().FusedScore.Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public void Rerank_DecayAbsent_RenormalizesOverBaseAndCentroidOnly()
    {
        var candidates = new[]
        {
            new RerankCandidate(1, BaseScore: 0.7, Centroid: CentroidCos1, Decay: null)
        };

        var results = _reranker.Rerank(Query, candidates);

        // (0.45*0.7 + 0.45*1.0) / 0.9 = 0.85
        var expected = (0.45 * 0.7 + 0.45 * 1.0) / 0.9;
        expected.Should().BeApproximately(0.5 * 0.7 + 0.5 * 1.0, 1e-6);
        results.Single().FusedScore.Should().BeApproximately(expected, 1e-9);
        results.Single().FusedScore.Should().BeApproximately(0.85, 1e-6);
    }

    [Fact]
    public void Rerank_BothCentroidAndDecayAbsent_FusedScoreEqualsBaseScoreExactly_AndOrderPreserved()
    {
        var candidates = new[]
        {
            new RerankCandidate(1, BaseScore: 0.9, Centroid: null, Decay: null),
            new RerankCandidate(2, BaseScore: 0.5, Centroid: null, Decay: null),
            new RerankCandidate(3, BaseScore: 0.2, Centroid: null, Decay: null)
        };

        var results = _reranker.Rerank(Query, candidates);

        results.Select(r => r.Id).Should().ContainInOrder(1UL, 2UL, 3UL);
        results[0].FusedScore.Should().Be(0.9);
        results[1].FusedScore.Should().Be(0.5);
        results[2].FusedScore.Should().Be(0.2);
    }

    [Fact]
    public void Rerank_CentroidWrongLength_TreatedAsAbsentNotZero()
    {
        var wrongLengthCentroid = new float[] { 1f, 0f, 0f }; // Query has length 2.

        var candidates = new[]
        {
            new RerankCandidate(1, BaseScore: 0.8, Centroid: wrongLengthCentroid, Decay: 0.6)
        };

        var results = _reranker.Rerank(Query, candidates);

        // Same as centroid-absent case: (0.45*0.8 + 0.1*0.6) / 0.55.
        var expected = (0.45 * 0.8 + 0.1 * 0.6) / 0.55;
        results.Single().FusedScore.Should().BeApproximately(expected, 1e-9);
    }
}
