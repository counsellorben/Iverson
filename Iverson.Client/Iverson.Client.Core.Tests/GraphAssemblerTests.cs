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
}
