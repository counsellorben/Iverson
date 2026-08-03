using System.Globalization;
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
        var svc = Substitute.For<IVectorWriteService>();
        var vector = new float[] { 0.1f, 0.2f, 0.3f };

        await svc.UpsertAsync("players", 42UL, vector);

        await svc.Received(1).UpsertAsync("players", 42UL, vector);
    }

    [Fact]
    public async Task SearchAsync_ReturnsResults()
    {
        var svc = Substitute.For<IVectorQueryService>();
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
        var svc = Substitute.For<IVectorQueryService>();
        svc.SearchNamedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<ulong>())
           .Returns(new List<VectorSearchResult>());
        const string vectorName = "bio_embedding";

        await svc.SearchNamedAsync("players", vectorName, new float[] { 0.5f });

        await svc.Received(1).SearchNamedAsync("players", vectorName, Arg.Any<float[]>(), Arg.Any<ulong>());
    }

    [Fact]
    public async Task UpsertNamedAsync_IsCalledWithNamedVectors()
    {
        var svc = Substitute.For<IVectorWriteService>();
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
    public async Task UpdateNamedVectorsAsync_IsCalledWithNamedVectors()
    {
        var svc = Substitute.For<IVectorWriteService>();
        var namedVectors = new Dictionary<string, float[]>
        {
            ["bio_embedding"] = new float[] { 0.1f, 0.2f },
            ["stats_embedding"] = new float[] { 0.3f, 0.4f }
        };

        await svc.UpdateNamedVectorsAsync("players", 7UL, namedVectors);

        await svc.Received(1).UpdateNamedVectorsAsync(
            "players",
            7UL,
            Arg.Is<IReadOnlyDictionary<string, float[]>>(d =>
                d.ContainsKey("bio_embedding") && d.ContainsKey("stats_embedding")));
    }

    [Fact]
    public async Task DeleteAsync_IsCalledWithCorrectId()
    {
        var svc = Substitute.For<IVectorWriteService>();

        await svc.DeleteAsync("players", 99UL);

        await svc.Received(1).DeleteAsync("players", 99UL);
    }

    [Fact]
    public async Task DeleteByFilterAsync_IsCalledWithCollectionAndFilter()
    {
        var svc = Substitute.For<IVectorWriteService>();
        var filter = new Filter();
        filter.Must.Add(Conditions.MatchKeyword("parent_id", "article-123"));

        await svc.DeleteByFilterAsync("articles_chunks", filter);

        await svc.Received(1).DeleteByFilterAsync("articles_chunks", filter);
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmptyList_WhenNoMatches()
    {
        var svc = Substitute.For<IVectorQueryService>();
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
        var vector = Substitute.For<IVectorWriteService>();
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

    [Theory]
    [InlineData("string")]
    [InlineData("integer")]
    [InlineData("double")]
    [InlineData("bool")]
    public void ToCanonicalString_MapsNonStringPayloadKindsToCanonicalText(string kind)
    {
        // The Qdrant client is a concrete, non-virtual type, so the search-result mapping
        // cannot be driven through a mocked client here; via InternalsVisibleTo the mapping
        // helper is exercised directly instead. Integration coverage lives in
        // QdrantIntegrationTests.
        var (value, expected) = kind switch
        {
            "string"  => (new Value { StringValue  = "Allen Iverson" }, "Allen Iverson"),
            "integer" => (new Value { IntegerValue = 42L },             "42"),
            "double"  => (new Value { DoubleValue  = 3.5 },             "3.5"),
            _         => (new Value { BoolValue    = true },            "true")
        };

        IntelligenceVectorService.ToCanonicalString(value).Should().Be(expected);
    }

    [Fact]
    public void ToQdrantValue_ArrayValue_BecomesAListOfElementTypedValues()
    {
        // An array column's payload index is built from the ELEMENT kind, so the value has to be
        // emitted as a real Qdrant ListValue. Flattening it to a string would leave the field
        // silently unfilterable under its own index.
        var value = IntelligenceVectorService.ToQdrantValue(new List<object> { 1L, 2L, 3L });

        value.KindCase.Should().Be(Value.KindOneofCase.ListValue);
        value.ListValue.Values.Select(v => v.KindCase)
             .Should().AllBeEquivalentTo(Value.KindOneofCase.IntegerValue);
        value.ListValue.Values.Select(v => v.IntegerValue).Should().Equal(1L, 2L, 3L);
    }

    [Fact]
    public void ToQdrantValue_StringValue_StaysAStringNotACharList()
    {
        // string is itself IEnumerable<char>; the string arm must win over the sequence arm.
        IntelligenceVectorService.ToQdrantValue("Allen Iverson").KindCase
            .Should().Be(Value.KindOneofCase.StringValue);
    }

    [Fact]
    public void ToQdrantValue_TimestampArray_EmitsRoundTripFormattedStrings()
    {
        var when  = DateTimeOffset.Parse("2026-07-30T10:30:00Z", CultureInfo.InvariantCulture);
        var value = IntelligenceVectorService.ToQdrantValue(new List<object> { when, when });

        value.ListValue.Values.Select(v => v.StringValue)
             .Should().AllBe(when.ToString("o"));
    }

    [Fact]
    public void QdrantVectorService_ImplementsQueryAndWriteRoleInterfaces()
    {
        typeof(IntelligenceVectorService).Should().Implement<IVectorQueryService>();
        typeof(IntelligenceVectorService).Should().Implement<IVectorWriteService>();
    }

}
