using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Iverson.Client.Attributes;
using Iverson.Client.Contracts;
using Iverson.Client.Search;
using NSubstitute;
using Xunit;
using static Iverson.Client.Core.Tests.TestStreamHelper;

namespace Iverson.Client.Core.Tests;

public class EntityCoordinatorVectorSearchTests
{
    [IversonEntity]
    private sealed class TestArticle
    {
        [IversonKey]
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
    }

    [Fact]
    public async Task SearchSimilarAsync_StreamsResults()
    {
        var search = Substitute.For<ObjectSearchService.ObjectSearchServiceClient>();
        var responses = new List<SearchResponse> { new() { Score = 0.9f, Data = new Struct() } };
        search.SearchSimilar(Arg.Any<SearchSimilarRequest>(), cancellationToken: Arg.Any<CancellationToken>())
              .Returns(MakeCall(responses));

        var coordinator = TestCoordinatorFactory.Create<TestArticle>(search);

        var results = new List<SearchResult<TestArticle>>();
        await foreach (var r in coordinator.SearchSimilarAsync(Query.Similar<TestArticle>(a => a.Title).Text("q")))
            results.Add(r);

        results.Should().ContainSingle();
    }

    [Fact]
    public async Task SearchChunksAsync_StreamsResults()
    {
        var search = Substitute.For<ObjectSearchService.ObjectSearchServiceClient>();
        var responses = new List<ChunkSearchResponse>
        {
            new() { ParentKey = "parent-1", ChunkText = "some passage", Score = 0.75f }
        };
        search.SearchChunks(Arg.Any<SearchChunksRequest>(), cancellationToken: Arg.Any<CancellationToken>())
              .Returns(MakeCall(responses));

        var coordinator = TestCoordinatorFactory.Create<TestArticle>(search);

        var results = new List<ChunkSearchResponse>();
        await foreach (var r in coordinator.SearchChunksAsync(Query.Chunks<TestArticle>(a => a.Title).Text("q")))
            results.Add(r);

        results.Should().ContainSingle();
        results[0].ParentKey.Should().Be("parent-1");
    }
}
