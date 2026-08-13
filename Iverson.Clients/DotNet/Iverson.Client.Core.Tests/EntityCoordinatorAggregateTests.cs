using FluentAssertions;
using Grpc.Core;
using Iverson.Client.Attributes;
using Iverson.Client.Contracts;
using Iverson.Client.Search;
using NSubstitute;
using Xunit;

namespace Iverson.Client.Core.Tests;

public class EntityCoordinatorAggregateTests
{
    [IversonEntity]
    private sealed class TestArticle
    {
        [IversonKey]
        public string Id { get; set; } = "";
        [IversonTenant]
        public string TenantId { get; set; } = "";
        public string Category { get; set; } = "";
    }

    private static AsyncUnaryCall<AggregateResponse> MakeUnaryCall(AggregateResponse response) =>
        new(
            Task.FromResult(response),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });

    [Fact]
    public async Task AggregateAsync_ReturnsResponse()
    {
        var search = Substitute.For<ObjectSearchService.ObjectSearchServiceClient>();
        var response = new AggregateResponse { Total = 42 };
        response.Results.Add(new AggregationResult
        {
            Name        = "n",
            Type        = AggregationType.Count,
            MetricValue = 4
        });
        search.AggregateAsync(Arg.Any<AggregateRequest>(), Arg.Any<Metadata>(), cancellationToken: Arg.Any<CancellationToken>())
              .Returns(MakeUnaryCall(response));

        var coordinator = TestCoordinatorFactory.Create<TestArticle>(search);

        var result = await coordinator.AggregateAsync(new AggregateBuilder("TestArticle").CountAll("n"));

        result.Total.Should().Be(42);
        result.Results.Should().ContainSingle();
        result.Results[0].Name.Should().Be("n");
        result.Results[0].MetricValue.Should().Be(4);
    }

    [Fact]
    public async Task AggregateAsync_PassesSuppliedHeaders_ToStub()
    {
        var search = Substitute.For<ObjectSearchService.ObjectSearchServiceClient>();
        Metadata? capturedHeaders = null;
        search.AggregateAsync(
                Arg.Any<AggregateRequest>(),
                Arg.Do<Metadata>(h => capturedHeaders = h),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
              .Returns(MakeUnaryCall(new AggregateResponse()));

        var coordinator = TestCoordinatorFactory.Create<TestArticle>(search);
        var headers = new Metadata { { "x-trace-id", "Bearer test-token" } };

        await coordinator.AggregateAsync(new AggregateBuilder("TestArticle").CountAll("n"), headers);

        capturedHeaders.Should().NotBeNull();
        capturedHeaders!.Get("x-trace-id")!.Value.Should().Be("Bearer test-token");
    }

    [Fact]
    public async Task AggregateAsync_WithNoHeaders_EmitsNoActingUser()
    {
        var search = Substitute.For<ObjectSearchService.ObjectSearchServiceClient>();
        Metadata? capturedHeaders = null;
        search.AggregateAsync(
                Arg.Any<AggregateRequest>(),
                Arg.Do<Metadata>(h => capturedHeaders = h),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
              .Returns(MakeUnaryCall(new AggregateResponse()));

        var coordinator = TestCoordinatorFactory.Create<TestArticle>(search);

        await coordinator.AggregateAsync(new AggregateBuilder("TestArticle").CountAll("n"));

        capturedHeaders.Should().NotBeNull();
        capturedHeaders!.Get(ActingUserMetadata.MetadataKey).Should().BeNull();
    }
}
