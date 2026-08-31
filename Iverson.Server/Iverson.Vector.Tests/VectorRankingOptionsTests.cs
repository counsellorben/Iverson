using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iverson.Vector.Tests;

public sealed class VectorRankingOptionsTests
{
    private static IConfiguration BuildConfig(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e =>
                new KeyValuePair<string, string?>($"{VectorRankingOptions.Section}:{e.Key}", e.Value)))
            .Build();

    [Fact]
    public void ResultReranker_NonDefaultWeights_ProducesFusedScoreDifferentFromDefault()
    {
        var query = new[] { 1f, 0f };
        var candidates = new[]
        {
            new RerankCandidate(1, BaseScore: 0.6, Centroid: new[] { 1f, 0f }, Decay: 0.5)
        };

        var nonDefault = new ResultReranker(Options.Create(new VectorRankingOptions
        {
            WBase = 0.9,
            WCentroid = 0.1,
            WDecay = 0.0
        }));
        var @default = new ResultReranker(Options.Create(new VectorRankingOptions()));

        // (0.9*0.6 + 0.1*1.0 + 0.0*0.5) / 1.0 = 0.64
        var nonDefaultResult = nonDefault.Rerank(query, candidates).Single();
        nonDefaultResult.FusedScore.Should().BeApproximately(0.64, 1e-9);

        // (0.45*0.6 + 0.45*1.0 + 0.10*0.5) / 1.0 = 0.77
        var defaultResult = @default.Rerank(query, candidates).Single();
        defaultResult.FusedScore.Should().BeApproximately(0.77, 1e-9);

        nonDefaultResult.FusedScore.Should().NotBe(defaultResult.FusedScore);
    }

    [Fact]
    public void ResultDiversifier_NonDefaultLambda_ChangesSelectionOrderFromDefault()
    {
        // A selected first (unconditional). B is a near-duplicate of A (similarity 1.0);
        // C is dissimilar to A (similarity 0.0).
        var candidates = new[]
        {
            new DiversifyCandidate(1, Score: 1.00, DiversityVector: new float[] { 1f, 0f }),
            new DiversifyCandidate(2, Score: 0.95, DiversityVector: new float[] { 1f, 0f }),
            new DiversifyCandidate(3, Score: 0.60, DiversityVector: new float[] { 0f, 1f })
        };

        // Default Lambda = 0.70: MMR(B) = 0.7*0.95 - 0.3*1.0 = 0.365, MMR(C) = 0.7*0.60 = 0.42.
        // C wins, so the default pick order is [1, 3].
        var @default = new ResultDiversifier(Options.Create(new VectorRankingOptions()));
        var defaultResults = @default.Diversify(candidates, topK: 2);
        defaultResults.Select(r => r.Id).Should().ContainInOrder(1UL, 3UL);

        // Lambda = 0.99: MMR(B) = 0.99*0.95 - 0.01*1.0 = 0.9305, MMR(C) = 0.99*0.60 = 0.594.
        // B now wins, flipping the pick order to [1, 2].
        var nonDefault = new ResultDiversifier(Options.Create(new VectorRankingOptions { Lambda = 0.99 }));
        var nonDefaultResults = nonDefault.Diversify(candidates, topK: 2);
        nonDefaultResults.Select(r => r.Id).Should().ContainInOrder(1UL, 2UL);
    }

    [Fact]
    public void AddVectorRanking_NegativeWeight_Throws()
    {
        var config = BuildConfig(
            ("WBase", "-0.1"),
            ("WCentroid", "0.45"),
            ("WDecay", "0.10"),
            ("Lambda", "0.70"));

        var act = () => new ServiceCollection().AddVectorRanking(config);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AddVectorRanking_AllThreeWeightsZero_Throws()
    {
        var config = BuildConfig(
            ("WBase", "0"),
            ("WCentroid", "0"),
            ("WDecay", "0"),
            ("Lambda", "0.70"));

        var act = () => new ServiceCollection().AddVectorRanking(config);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AddVectorRanking_LambdaOutOfRange_Throws()
    {
        var config = BuildConfig(
            ("WBase", "0.45"),
            ("WCentroid", "0.45"),
            ("WDecay", "0.10"),
            ("Lambda", "1.5"));

        var act = () => new ServiceCollection().AddVectorRanking(config);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AddVectorRanking_NonFiniteWeight_NaN_Throws()
    {
        var config = BuildConfig(
            ("WBase", "NaN"),
            ("WCentroid", "0.45"),
            ("WDecay", "0.10"),
            ("Lambda", "0.70"));

        var act = () => new ServiceCollection().AddVectorRanking(config);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AddVectorRanking_NonFiniteWeight_Infinity_Throws()
    {
        var config = BuildConfig(
            ("WBase", "Infinity"),
            ("WCentroid", "0.45"),
            ("WDecay", "0.10"),
            ("Lambda", "0.70"));

        var act = () => new ServiceCollection().AddVectorRanking(config);

        act.Should().Throw<InvalidOperationException>();
    }
}
