using FluentAssertions;
using Grpc.Core;
using Iverson.Api.Grpc;
using Iverson.Embeddings;
using Iverson.Patterns;
using Iverson.Vector;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

[Trait("Category", "Integration")]
[Collection(ContainerCollection.Name)]
public sealed class SimilarityResolverIntegrationTests(QdrantGrpcContainerFixture fixture) : IClassFixture<QdrantGrpcContainerFixture>
{
    private readonly IntelligenceVectorService _vector = fixture.Service;
    private readonly IntelligenceCollectionManager _mgr = fixture.CollectionManager;
    private readonly IntelligenceTenantScope _scope = new("test-signing-key-0123456789abcdef");

    private async Task<string> CollectionWithAsync(string vectorName, params (ulong Id, float[] Vector)[] points)
    {
        var name = "sim_" + Guid.NewGuid().ToString("N")[..8];
        await _mgr.ApplyCollectionAsync(new CollectionSchema(name, [new NamedVector(vectorName, 4)], []));
        foreach (var (id, v) in points)
            await _vector.UpsertNamedAsync(name, id, new Dictionary<string, float[]> { [vectorName] = v });
        return name;
    }

    private static readonly float[] Query = [1f, 0f, 0f, 0f];

    [Fact]
    public async Task Scores_every_present_vector_and_leaves_an_absent_point_null()
    {
        var col = await CollectionWithAsync("title_vector", (1, [1f, 0f, 0f, 0f]), (2, [0f, 1f, 0f, 0f]));

        var scores = await new SimilarityResolver(_vector, _scope)
            .ScoreRowsAsync(col, [1, 2, 3], ["title_vector"], [Query], CancellationToken.None);

        scores[0][0].Should().BeApproximately(1.0, 1e-6);
        scores[1][0].Should().BeApproximately(0.0, 1e-6);
        scores[2][0].Should().BeNull();
    }

    [Fact]
    public async Task A_stored_zero_vector_scores_NaN()
    {
        var col = await CollectionWithAsync("title_vector", (1, [0f, 0f, 0f, 0f]));

        var scores = await new SimilarityResolver(_vector, _scope)
            .ScoreRowsAsync(col, [1], ["title_vector"], [Query], CancellationToken.None);

        scores[0][0].Should().Be(double.NaN);
    }

    [Fact]
    public async Task A_missing_object_collection_or_an_unconfigured_vector_scores_null()
    {
        var resolver = new SimilarityResolver(_vector, _scope);

        var missing = await resolver.ScoreRowsAsync("sim_missing_" + Guid.NewGuid().ToString("N")[..8],
            [1], ["title_vector"], [Query], CancellationToken.None);
        missing[0][0].Should().BeNull();

        var col = await CollectionWithAsync("other_vector", (1, [1f, 0f, 0f, 0f]));
        var unconfigured = await resolver.ScoreRowsAsync(col, [1], ["title_vector"], [Query], CancellationToken.None);
        unconfigured[0][0].Should().BeNull();
    }

    [Fact]
    public async Task Each_distinct_vector_name_is_retrieved_once_per_batch()
    {
        var col = await CollectionWithAsync("title_vector", (1, [1f, 0f, 0f, 0f]));
        var spy = Substitute.For<IVectorQueryService>();
        spy.RetrieveNamedVectorAsync(default!, default!, default!, default).ReturnsForAnyArgs(ci =>
            _vector.RetrieveNamedVectorAsync(ci.ArgAt<string>(0), ci.ArgAt<IReadOnlyList<ulong>>(1), ci.ArgAt<string>(2), ci.ArgAt<CancellationToken>(3)));

        var scores = await new SimilarityResolver(spy, _scope)
            .ScoreRowsAsync(col, [1, 1], ["title_vector", "title_vector"], [Query, [0f, 1f, 0f, 0f]], CancellationToken.None);

        await spy.Received(1).RetrieveNamedVectorAsync(col, Arg.Any<IReadOnlyList<ulong>>(), "title_vector", Arg.Any<CancellationToken>());
        scores[1][0].Should().BeApproximately(1.0, 1e-6);
        scores[1][1].Should().BeApproximately(0.0, 1e-6);
    }

    [Theory]
    [InlineData(StatusCode.Unavailable, "down")]
    [InlineData(StatusCode.InvalidArgument, "Wrong input: some other problem")]
    public async Task Any_other_retrieve_failure_propagates(StatusCode code, string detail)
    {
        var failing = Substitute.For<IVectorQueryService>();
        failing.RetrieveNamedVectorAsync(default!, default!, default!, default)
            .ThrowsAsyncForAnyArgs(new RpcException(new Status(code, detail)));

        var act = () => new SimilarityResolver(failing, _scope)
            .ScoreRowsAsync("col", [1], ["title_vector"], [Query], CancellationToken.None);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(code);
    }

    [Fact]
    public void Chunk_vectors_score_against_every_term_and_an_absent_vector_scores_null()
    {
        var scores = SimilarityResolver.ScoreVectors([[1f, 0f, 0f, 0f], null], [Query, [0f, 1f, 0f, 0f]]);

        scores[0][0].Should().BeApproximately(1.0, 1e-6);
        scores[0][1].Should().BeApproximately(0.0, 1e-6);
        scores[1].Should().Equal(new double?[] { null, null });
    }

    [Fact]
    public async Task Each_distinct_text_is_embedded_once()
    {
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.EmbedQueryAsync("refund", Arg.Any<CancellationToken>()).Returns([1f, 0f, 0f, 0f]);
        embedding.EmbedQueryAsync("cancel", Arg.Any<CancellationToken>()).Returns([0f, 1f, 0f, 0f]);

        var vectors = await SimilarityResolver.EmbedAsync(embedding,
            [new SimilarityTerm("title", "refund"), new SimilarityTerm("body", "refund"), new SimilarityTerm("title", "cancel")],
            CancellationToken.None);

        vectors.Select(v => v[0]).Should().Equal(1f, 1f, 0f);
        await embedding.Received(1).EmbedQueryAsync("refund", Arg.Any<CancellationToken>());
        await embedding.Received(1).EmbedQueryAsync("cancel", Arg.Any<CancellationToken>());
    }
}
