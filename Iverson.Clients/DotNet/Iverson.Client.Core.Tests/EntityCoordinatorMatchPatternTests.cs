using System.Diagnostics;
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

public class EntityCoordinatorMatchPatternTests
{
    [IversonEntity]
    private sealed class TestArticle
    {
        [IversonKey]
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
    }

    private static MatchPatternResponse Response(string label, double n, long matchNumber, string classifier)
    {
        var data = new Struct();
        data.Fields["Label"] = Value.ForString(label);
        data.Fields["n"] = Value.ForNumber(n);
        return new MatchPatternResponse { Data = data, MatchNumber = matchNumber, Classifier = classifier };
    }

    [Fact]
    public async Task MatchPatternAsync_StreamsRowsWithMatchNumberAndClassifier()
    {
        var search = Substitute.For<ObjectSearchService.ObjectSearchServiceClient>();
        var responses = new List<MatchPatternResponse> { Response("x", 3, 1, ""), Response("y", 2, 2, "B") };
        search.MatchPattern(Arg.Any<MatchPatternRequest>(), Arg.Any<Metadata>(), cancellationToken: Arg.Any<CancellationToken>())
              .Returns(MakeCall(responses));

        var coordinator = TestCoordinatorFactory.Create<TestArticle>(search);

        var rows = new List<MatchPatternResult>();
        await foreach (var row in coordinator.MatchPatternAsync(Query.MatchPattern<TestArticle>().Pattern("A B+")))
            rows.Add(row);

        rows.Should().HaveCount(2);
        rows[0].Data["Label"].Should().Be("x");
        rows[0].Data["n"].Should().Be(3d);
        rows[0].MatchNumber.Should().Be(1);
        rows[0].Classifier.Should().BeEmpty();
        rows[1].Data["Label"].Should().Be("y");
        rows[1].MatchNumber.Should().Be(2);
        rows[1].Classifier.Should().Be("B");
    }

    [Fact]
    public async Task MatchPatternAsync_SendsTheBuildersRequest()
    {
        var search = Substitute.For<ObjectSearchService.ObjectSearchServiceClient>();
        MatchPatternRequest? captured = null;
        search.MatchPattern(
                Arg.Do<MatchPatternRequest>(r => captured = r),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
              .Returns(MakeCall(new List<MatchPatternResponse>()));

        var coordinator = TestCoordinatorFactory.Create<TestArticle>(search);
        var pattern = Query.MatchPattern<TestArticle>()
            .PartitionBy("Label")
            .OrderBy("Id")
            .Pattern("A B+")
            .Define("B", "Id > PREV(Id)")
            .Measure("n", "COUNT(*)")
            .Limit(7);

        await foreach (var _ in coordinator.MatchPatternAsync(pattern)) { }

        captured.Should().NotBeNull();
        captured!.Should().Be(pattern.Build(captured.TraceId));
    }

    [Fact]
    public async Task MatchPatternAsync_TakesTraceIdFromAmbientActivity()
    {
        var search = Substitute.For<ObjectSearchService.ObjectSearchServiceClient>();
        MatchPatternRequest? captured = null;
        search.MatchPattern(
                Arg.Do<MatchPatternRequest>(r => captured = r),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
              .Returns(MakeCall(new List<MatchPatternResponse>()));

        var coordinator = TestCoordinatorFactory.Create<TestArticle>(search);

        using var activity = new Activity("match-pattern-test").Start();
        await foreach (var _ in coordinator.MatchPatternAsync(Query.MatchPattern<TestArticle>().Pattern("A"))) { }

        captured.Should().NotBeNull();
        captured!.TraceId.Should().NotBeEmpty().And.Be(activity.TraceId.ToString());
    }

    [Fact]
    public async Task MatchPatternAsync_SendsTheBoundActingUser()
    {
        // Catches the call passing a fresh Metadata instead of the resolved headers: the server
        // would then evaluate the pattern with no end-user identity at all.
        var search = Substitute.For<ObjectSearchService.ObjectSearchServiceClient>();
        Metadata? capturedHeaders = null;
        search.MatchPattern(
                Arg.Any<MatchPatternRequest>(),
                Arg.Do<Metadata>(h => capturedHeaders = h),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
              .Returns(MakeCall(new List<MatchPatternResponse>()));

        var coordinator = TestCoordinatorFactory.Create<TestArticle>(search)
            .WithActingUser(() => Task.FromResult("bound-token"));

        await foreach (var _ in coordinator.MatchPatternAsync(Query.MatchPattern<TestArticle>().Pattern("A"))) { }

        capturedHeaders.Should().NotBeNull();
        capturedHeaders!.GetValue(ActingUserMetadata.MetadataKey).Should().Be("Bearer bound-token");
    }
}
