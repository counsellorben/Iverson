using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Client.Attributes;
using Iverson.Client.Contracts;
using Iverson.Client.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Client.Core.Tests;

// ── Test entity fixtures ──────────────────────────────────────────────────────

[IversonEntity]
internal sealed class TestTag
{
    [IversonKey]
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

[IversonEntity]
internal sealed class TestArticle
{
    [IversonKey]
    public string Id { get; set; } = string.Empty;
    public string TagId { get; set; } = string.Empty;

    [ManyToOne(typeof(TestTag), foreignKey: "TagId")]
    public TestTag? Tag { get; set; }
}

// ── Fake stream reader ────────────────────────────────────────────────────────

internal sealed class FakeAsyncStreamReader<T>(IEnumerable<T> items) : IAsyncStreamReader<T>
{
    private readonly IEnumerator<T> _enumerator = items.GetEnumerator();

    public T Current => _enumerator.Current;

    public Task<bool> MoveNext(CancellationToken cancellationToken)
        => Task.FromResult(_enumerator.MoveNext());
}

// ── Tests ─────────────────────────────────────────────────────────────────────

public class GraphAssemblerTests
{
    private static EntityRegistry BuildRegistry()
        => new([typeof(TestArticle).Assembly]);

    private static Struct MakeTagStruct(string id, string label)
    {
        var s = new Struct();
        s.Fields["id"]    = Value.ForString(id);
        s.Fields["label"] = Value.ForString(label);
        return s;
    }

    private static void SetupGetManyStream(
        ObjectRetrievalService.ObjectRetrievalServiceClient retrieval,
        IEnumerable<Struct> dataStructs)
    {
        var responses = dataStructs
            .Select(s => new RetrievalResponse { Found = true, Data = s })
            .ToList();

        var fakeStream = new FakeAsyncStreamReader<RetrievalResponse>(responses);
        var call = new AsyncServerStreamingCall<RetrievalResponse>(
            fakeStream,
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });

        retrieval.GetMany(Arg.Any<RetrievalManyRequest>(), Arg.Any<CallOptions>())
                 .Returns(call);
    }

    // Two Article entities each referencing the same Tag via TagId (ManyToOne)
    [Fact]
    public async Task AssembleManyAsync_ManyToOne_IssuesOneBatchCallNotN()
    {
        // Arrange
        var retrieval = Substitute.For<ObjectRetrievalService.ObjectRetrievalServiceClient>();
        var registry  = BuildRegistry();

        // Two articles, same TagId
        var tagId    = Guid.NewGuid().ToString();
        var article1 = new TestArticle { Id = Guid.NewGuid().ToString(), TagId = tagId };
        var article2 = new TestArticle { Id = Guid.NewGuid().ToString(), TagId = tagId };

        // Set up the streaming GetMany response for the Tag lookup
        var tagStruct = new Struct();
        tagStruct.Fields["id"]    = Value.ForString(tagId);
        tagStruct.Fields["label"] = Value.ForString("dotnet");

        var fakeStream = new FakeAsyncStreamReader<RetrievalResponse>(new[]
        {
            new RetrievalResponse { Found = true, Data = tagStruct }
        });

        var call = new AsyncServerStreamingCall<RetrievalResponse>(
            fakeStream,
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });

        retrieval.GetMany(Arg.Any<RetrievalManyRequest>(), Arg.Any<CallOptions>())
                 .Returns(call);

        var assembler = new GraphAssembler(retrieval, registry, NullLogger<GraphAssembler>.Instance);

        // Act
        var entities = new List<TestArticle> { article1, article2 };
        await assembler.AssembleManyAsync(entities);

        // Assert — one GetMany call total (not two)
        retrieval.Received(1).GetMany(Arg.Any<RetrievalManyRequest>(), Arg.Any<CallOptions>());
        article1.Tag.Should().NotBeNull();
        article2.Tag.Should().NotBeNull();
        article1.Tag!.Label.Should().Be("dotnet");
    }

    [Fact]
    public async Task AssembleManyAsync_ManyToOne_EachEntityGetsIndependentNavigationInstance()
    {
        // Both articles share the same TagId
        // After assembly, mutating article1.Tag must not affect article2.Tag
        var retrieval = Substitute.For<ObjectRetrievalService.ObjectRetrievalServiceClient>();
        var registry  = BuildRegistry();
        var tagId     = Guid.NewGuid().ToString();

        var article1 = new TestArticle { Id = Guid.NewGuid().ToString(), TagId = tagId };
        var article2 = new TestArticle { Id = Guid.NewGuid().ToString(), TagId = tagId };

        var tagStruct = MakeTagStruct(tagId, "original");
        SetupGetManyStream(retrieval, new[] { tagStruct });

        var assembler = new GraphAssembler(retrieval, registry, NullLogger<GraphAssembler>.Instance);
        await assembler.AssembleManyAsync(new List<TestArticle> { article1, article2 });

        // Mutate via article1 — article2 must be unaffected
        article1.Tag!.Label = "mutated";
        article2.Tag!.Label.Should().Be("original");
    }
}
