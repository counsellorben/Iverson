using FluentAssertions;
using NSubstitute;
using Qdrant.Client.Grpc;
using Xunit;

namespace Iverson.Vector.Tests;

public sealed class QdrantVectorServiceTests
{
    // ─── Interface contract tests (mocked) ───────────────────────────────────

    [Fact]
    public async Task UpsertAsync_IsCalledWithCorrectCollectionAndId()
    {
        var svc = Substitute.For<IVectorService>();
        var vector = new float[] { 0.1f, 0.2f, 0.3f };

        await svc.UpsertAsync("players", 42UL, vector);

        await svc.Received(1).UpsertAsync("players", 42UL, vector);
    }

    [Fact]
    public async Task SearchAsync_ReturnsResults()
    {
        var svc = Substitute.For<IVectorService>();
        var expected = new List<VectorSearchResult>
        {
            new(1UL, 0.95, new Dictionary<string, string> { ["name"] = "Allen Iverson" }),
            new(2UL, 0.88, new Dictionary<string, string> { ["name"] = "Kobe Bryant" })
        };
        svc.SearchAsync(Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<ulong>()).Returns(expected);

        var result = await svc.SearchAsync("players", new float[] { 0.1f, 0.2f });

        result.Should().HaveCount(2);
        result.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task SearchNamedAsync_IsCalledWithVectorName()
    {
        var svc = Substitute.For<IVectorService>();
        svc.SearchNamedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<ulong>())
           .Returns(new List<VectorSearchResult>());
        const string vectorName = "bio_embedding";

        await svc.SearchNamedAsync("players", vectorName, new float[] { 0.5f });

        await svc.Received(1).SearchNamedAsync("players", vectorName, Arg.Any<float[]>(), Arg.Any<ulong>());
    }

    [Fact]
    public async Task UpsertNamedAsync_IsCalledWithNamedVectors()
    {
        var svc = Substitute.For<IVectorService>();
        var namedVectors = new Dictionary<string, float[]>
        {
            ["bio_embedding"] = new float[] { 0.1f, 0.2f },
            ["stats_embedding"] = new float[] { 0.3f, 0.4f }
        };

        await svc.UpsertNamedAsync("players", 7UL, namedVectors);

        await svc.Received(1).UpsertNamedAsync(
            "players",
            7UL,
            Arg.Is<IReadOnlyDictionary<string, float[]>>(d =>
                d.ContainsKey("bio_embedding") && d.ContainsKey("stats_embedding")),
            Arg.Any<IReadOnlyDictionary<string, object>?>());
    }

    [Fact]
    public async Task DeleteAsync_IsCalledWithCorrectId()
    {
        var svc = Substitute.For<IVectorService>();

        await svc.DeleteAsync("players", 99UL);

        await svc.Received(1).DeleteAsync("players", 99UL);
    }

    [Fact]
    public async Task DeleteByFilterAsync_IsCalledWithCollectionAndFilter()
    {
        var svc = Substitute.For<IVectorService>();
        var filter = new Filter();
        filter.Must.Add(Conditions.MatchKeyword("parent_id", "article-123"));

        await svc.DeleteByFilterAsync("articles_chunks", filter);

        await svc.Received(1).DeleteByFilterAsync("articles_chunks", filter);
    }

    [Fact]
    public async Task ApplyCollectionAsync_IsCalledWithSchema()
    {
        var svc = Substitute.For<IVectorService>();
        var schema = new CollectionSchema(
            "players",
            new List<NamedVector> { new("bio_embedding", 1536) },
            new List<PayloadIndex> { new("team", PayloadIndexKind.Keyword) });

        await svc.ApplyCollectionAsync(schema);

        await svc.Received(1).ApplyCollectionAsync(
            Arg.Is<CollectionSchema>(s => s.CollectionName == "players"));
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmptyList_WhenNoMatches()
    {
        var svc = Substitute.For<IVectorService>();
        svc.SearchAsync(Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<ulong>())
           .Returns(new List<VectorSearchResult>());

        var result = await svc.SearchAsync("players", new float[] { 0.1f });

        result.Should().BeEmpty();
    }

    // ─── Schema model tests (pure value, no mocks) ───────────────────────────

    [Fact]
    public void CollectionSchema_StoresAllFields()
    {
        var vectors = new List<NamedVector> { new("bio_embedding", 768) };
        var indexes = new List<PayloadIndex> { new("team", PayloadIndexKind.Keyword) };
        var schema = new CollectionSchema("athletes", vectors, indexes);

        schema.CollectionName.Should().Be("athletes");
        schema.Vectors.Should().HaveCount(1);
        schema.PayloadIndexes.Should().HaveCount(1);
    }

    [Fact]
    public void NamedVector_DimensionIsPreserved()
    {
        var nv = new NamedVector("stats_embedding", 512);

        nv.Name.Should().Be("stats_embedding");
        nv.Dimension.Should().Be(512);
    }

    [Fact]
    public void VectorSearchResult_PayloadIsAccessible()
    {
        var payload = new Dictionary<string, string> { ["position"] = "PG", ["team"] = "76ers" };
        var result = new VectorSearchResult(3UL, 0.99, payload);

        result.Id.Should().Be(3UL);
        result.Score.Should().Be(0.99);
        result.Payload["position"].Should().Be("PG");
        result.Payload["team"].Should().Be("76ers");
    }

    [Fact]
    public void PayloadIndex_StoresFieldNameAndKind()
    {
        var index = new PayloadIndex("year", PayloadIndexKind.Integer);

        index.FieldName.Should().Be("year");
        index.Kind.Should().Be(PayloadIndexKind.Integer);
    }

    [Fact]
    public async Task UpsertAsync_AcceptsTypedPayloadValues()
    {
        var vector = Substitute.For<IVectorService>();
        var payload = new Dictionary<string, object>
        {
            ["title"]     = "typed",
            ["wordCount"] = 42L,
            ["rating"]    = 4.5,
            ["published"] = true
        };

        await vector.UpsertAsync("articles", 1, [0.1f, 0.2f], payload);

        await vector.Received(1).UpsertAsync("articles", 1, Arg.Any<float[]>(),
            Arg.Is<IReadOnlyDictionary<string, object>>(p =>
                p["title"].Equals("typed") && p["wordCount"].Equals(42L) &&
                p["rating"].Equals(4.5) && p["published"].Equals(true)));
    }

}
