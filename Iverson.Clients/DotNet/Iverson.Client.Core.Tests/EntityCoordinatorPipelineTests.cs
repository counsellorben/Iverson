using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Value = Google.Protobuf.WellKnownTypes.Value;
using Grpc.Core;
using Iverson.Client.Attributes;
using Iverson.Client.Contracts;
using Iverson.Client.Search;
using NSubstitute;
using Xunit;
using static Iverson.Client.Core.Tests.TestStreamHelper;

namespace Iverson.Client.Core.Tests;

public class EntityCoordinatorPipelineTests
{
    [IversonEntity]
    private sealed class TestArticle
    {
        [IversonKey]
        public string Id { get; set; } = "";
        public string AuthorId { get; set; } = "";
    }

    private sealed class AuthorArticleCount
    {
        public string AuthorId { get; set; } = "";
        public long Articles { get; set; }
    }

    [Fact]
    public async Task PipelineAsync_StreamsUntypedRows()
    {
        var search = Substitute.For<ObjectSearchService.ObjectSearchServiceClient>();
        var data = new Struct();
        data.Fields["authorId"] = Value.ForString("A");
        var responses = new List<SearchResponse> { new() { Data = data } };
        search.Pipeline(Arg.Any<PipelineRequest>(), Arg.Any<Metadata>(), cancellationToken: Arg.Any<CancellationToken>())
              .Returns(MakeCall(responses));

        var coordinator = TestCoordinatorFactory.Create<TestArticle>(search);

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await foreach (var row in coordinator.PipelineAsync(Query.Pipeline<TestArticle>()))
            rows.Add(row);

        rows.Should().ContainSingle();
        rows[0]["authorId"].Should().Be("A");
    }

    [Fact]
    public async Task PipelineAsyncTyped_ProjectsOntoResultType()
    {
        var search = Substitute.For<ObjectSearchService.ObjectSearchServiceClient>();
        var data = new Struct();
        data.Fields["authorId"] = Value.ForString("A");
        data.Fields["articles"] = Value.ForNumber(4);
        var responses = new List<SearchResponse> { new() { Data = data } };
        search.Pipeline(Arg.Any<PipelineRequest>(), Arg.Any<Metadata>(), cancellationToken: Arg.Any<CancellationToken>())
              .Returns(MakeCall(responses));

        var coordinator = TestCoordinatorFactory.Create<TestArticle>(search);

        var rows = new List<AuthorArticleCount>();
        await foreach (var row in coordinator.PipelineAsync<AuthorArticleCount>(Query.Pipeline<TestArticle>()))
            rows.Add(row);

        rows.Should().ContainSingle(r => r.AuthorId == "A" && r.Articles == 4);
    }

    // Both overloads resolve their own headers and call search.Pipeline separately, so each is
    // pinned. Catches either call passing a fresh Metadata instead of the resolved headers: the
    // server would then run the pipeline with no end-user identity at all.

    private static Func<Metadata?> CapturePipelineHeaders(ObjectSearchService.ObjectSearchServiceClient search)
    {
        Metadata? captured = null;
        search.Pipeline(
                Arg.Any<PipelineRequest>(),
                Arg.Do<Metadata>(h => captured = h),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
              .Returns(MakeCall(new List<SearchResponse>()));
        return () => captured;
    }

    [Fact]
    public async Task PipelineAsync_SendsTheBoundActingUser()
    {
        var search = Substitute.For<ObjectSearchService.ObjectSearchServiceClient>();
        var headers = CapturePipelineHeaders(search);

        var coordinator = TestCoordinatorFactory.Create<TestArticle>(search)
            .WithActingUser(() => Task.FromResult("bound-token"));

        await foreach (var _ in coordinator.PipelineAsync(Query.Pipeline<TestArticle>())) { }

        headers().Should().NotBeNull();
        headers()!.GetValue(ActingUserMetadata.MetadataKey).Should().Be("Bearer bound-token");
    }

    [Fact]
    public async Task PipelineAsyncTyped_SendsTheBoundActingUser()
    {
        var search = Substitute.For<ObjectSearchService.ObjectSearchServiceClient>();
        var headers = CapturePipelineHeaders(search);

        var coordinator = TestCoordinatorFactory.Create<TestArticle>(search)
            .WithActingUser(() => Task.FromResult("bound-token"));

        await foreach (var _ in coordinator.PipelineAsync<AuthorArticleCount>(Query.Pipeline<TestArticle>())) { }

        headers().Should().NotBeNull();
        headers()!.GetValue(ActingUserMetadata.MetadataKey).Should().Be("Bearer bound-token");
    }
}
