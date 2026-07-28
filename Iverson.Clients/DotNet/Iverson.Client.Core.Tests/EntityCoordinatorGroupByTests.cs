using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Client.Attributes;
using Iverson.Client.Contracts;
using Iverson.Client.Search;
using NSubstitute;
using Xunit;
using static Iverson.Client.Core.Tests.TestStreamHelper;

namespace Iverson.Client.Core.Tests;

public class EntityCoordinatorGroupByTests
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

    [Fact]
    public async Task GroupByAsync_StreamsUntypedRows()
    {
        var search = Substitute.For<ObjectSearchService.ObjectSearchServiceClient>();
        var data = new Struct();
        data.Fields["category"] = Value.ForString("tech");
        data.Fields["n"] = Value.ForNumber(4);
        var responses = new List<SearchResponse> { new() { Data = data } };
        search.GroupBy(Arg.Any<GroupByRequest>(), cancellationToken: Arg.Any<CancellationToken>())
              .Returns(MakeCall(responses));

        var coordinator = TestCoordinatorFactory.Create<TestArticle>(search);

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await foreach (var row in coordinator.GroupByAsync(Query.GroupBy("TestArticle").Keys("Category").CountAll("n")))
            rows.Add(row);

        rows.Should().ContainSingle();
        rows[0]["category"].Should().Be("tech");
        rows[0]["n"].Should().Be(4d);
    }

    [Fact]
    public async Task GroupByAsync_PassesSuppliedHeaders_ToStub()
    {
        var search = Substitute.For<ObjectSearchService.ObjectSearchServiceClient>();
        Metadata? capturedHeaders = null;
        search.GroupBy(
                Arg.Any<GroupByRequest>(),
                Arg.Do<Metadata>(h => capturedHeaders = h),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
              .Returns(MakeCall(new List<SearchResponse>()));

        var coordinator = TestCoordinatorFactory.Create<TestArticle>(search);
        var headers = new Metadata { { "x-acting-user-authorization", "Bearer test-token" } };

        await foreach (var _ in coordinator.GroupByAsync(Query.GroupBy("TestArticle").CountAll("n"), headers)) { }

        capturedHeaders.Should().NotBeNull();
        capturedHeaders!.Get("x-acting-user-authorization")!.Value.Should().Be("Bearer test-token");
    }

    [Fact]
    public async Task GroupByAsync_WithNoHeaders_PassesNull()
    {
        var search = Substitute.For<ObjectSearchService.ObjectSearchServiceClient>();
        Metadata? capturedHeaders = null;
        search.GroupBy(
                Arg.Any<GroupByRequest>(),
                Arg.Do<Metadata>(h => capturedHeaders = h),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
              .Returns(MakeCall(new List<SearchResponse>()));

        var coordinator = TestCoordinatorFactory.Create<TestArticle>(search);

        await foreach (var _ in coordinator.GroupByAsync(Query.GroupBy("TestArticle").CountAll("n"))) { }

        capturedHeaders.Should().BeNull();
    }
}
