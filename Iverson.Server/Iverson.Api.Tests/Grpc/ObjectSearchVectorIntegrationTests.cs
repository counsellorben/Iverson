using DotNet.Testcontainers.Builders;
using FluentAssertions;
using Grpc.Core;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.Sql;
using Iverson.StarRocks;
using Iverson.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Qdrant.Client;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

public sealed class QdrantGrpcContainerFixture : IAsyncLifetime
{
    private const int GrpcPort = 6334;
    private readonly DotNet.Testcontainers.Containers.IContainer _container =
        new ContainerBuilder()
            .WithImage("qdrant/qdrant:v1.13.6")
            .WithPortBinding(GrpcPort, assignRandomHostPort: true)
            .WithPortBinding(6333, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(GrpcPort))
            .Build();

    public QdrantVectorService Service { get; private set; } = null!;
    public QdrantCollectionManager CollectionManager { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var client = new QdrantClient(_container.Hostname, _container.GetMappedPublicPort(GrpcPort), https: false);
        Service           = new QdrantVectorService(client, NullLogger<QdrantVectorService>.Instance);
        CollectionManager = new QdrantCollectionManager(client, NullLogger<QdrantCollectionManager>.Instance);
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[Trait("Category", "Integration")]
public sealed class ObjectSearchVectorIntegrationTests : IClassFixture<QdrantGrpcContainerFixture>
{
    private readonly QdrantVectorService _vector;
    private readonly QdrantCollectionManager _mgr;
    private readonly IEmbeddingService _embedding = Substitute.For<IEmbeddingService>();
    private readonly SchemaRegistry _registry;

    public ObjectSearchVectorIntegrationTests(QdrantGrpcContainerFixture fx)
    {
        _vector = fx.Service;
        _mgr    = fx.CollectionManager;
        var sql = Substitute.For<IPostgresQueryExecutor>();
        sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);
        _registry = new SchemaRegistry(sql, NullLogger<SchemaRegistry>.Instance);
    }

    private static string UniqueName() => "art_" + Guid.NewGuid().ToString("N")[..8];

    private ObjectSearchGrpcService BuildSut() =>
        new(_registry, Substitute.For<IStarRocksQueryExecutor>(), _vector, _embedding,
            NullLogger<ObjectSearchGrpcService>.Instance);

    private static (IServerStreamWriter<T> writer, List<T> written) MakeStream<T>()
    {
        var written = new List<T>();
        var writer  = Substitute.For<IServerStreamWriter<T>>();
        writer.WriteAsync(Arg.Do<T>(written.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return (writer, written);
    }

    [Fact]
    public async Task SearchSimilar_WithRangeFilter_ReturnsOnlyMatchingTypedPayload()
    {
        var collection = UniqueName();
        var baseSchema = SchemaFixtures.ArticleSchema();
        var schema = baseSchema with
        {
            CollectionName = collection,
            // SchemaFixtures.ArticleSchema() only declares Title/Body; add WordCount so
            // ValidateFilterProperty accepts it as a real scalar column for this test.
            ScalarColumns = [.. baseSchema.ScalarColumns, new ColumnDescriptor("WordCount", "integer", false)]
        };
        await _registry.RegisterAsync(schema);
        await _mgr.ApplyCollectionAsync(new CollectionSchema(
            collection, [new NamedVector("title_vector", 4)], []));

        var vec = new float[] { 0.1f, 0.2f, 0.3f, 0.4f };
        _embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(vec);

        await _vector.UpsertNamedAsync(collection, 1,
            new Dictionary<string, float[]> { ["title_vector"] = vec },
            new Dictionary<string, object> { ["wordCount"] = 100L });
        await _vector.UpsertNamedAsync(collection, 2,
            new Dictionary<string, float[]> { ["title_vector"] = vec },
            new Dictionary<string, object> { ["wordCount"] = 900L });

        var sut = BuildSut();
        var request = new SearchSimilarRequest { TypeName = "Article", Property = "Title", Query = "q", TopK = 10 };
        request.Filter.Add(new SearchClause
        {
            Property = "WordCount", Operator = SearchOperator.GreaterThan,
            Value = new SearchValue { NumberVal = 500 }, ClauseType = SearchClauseType.Filter
        });

        var (writer, written) = MakeStream<SearchResponse>();
        await sut.SearchSimilar(request, writer, TestServerCallContext.Create());

        written.Should().ContainSingle();
    }

    [Fact]
    public async Task SearchChunks_WithPkEqualsFilter_ReturnsOnlyThatParentsChunks()
    {
        var collection = UniqueName();
        var schema = SchemaFixtures.ArticleSchema() with { CollectionName = collection };
        await _registry.RegisterAsync(schema);

        var chunksCollection = collection + "_chunks";
        await _mgr.ApplyCollectionAsync(new CollectionSchema(
            chunksCollection, [new NamedVector("body_vector", 4)],
            [new PayloadIndex("parent_id", PayloadIndexKind.Keyword)]));

        var vec = new float[] { 0.1f, 0.2f, 0.3f, 0.4f };
        _embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(vec);

        await _vector.UpsertNamedAsync(chunksCollection, 1,
            new Dictionary<string, float[]> { ["body_vector"] = vec },
            new Dictionary<string, object> { ["text"] = "chunk from parent A", ["parent_id"] = "parent-a" });
        await _vector.UpsertNamedAsync(chunksCollection, 2,
            new Dictionary<string, float[]> { ["body_vector"] = vec },
            new Dictionary<string, object> { ["text"] = "chunk from parent B", ["parent_id"] = "parent-b" });

        var sut = BuildSut();
        var request = new SearchChunksRequest { TypeName = "Article", Property = "Body", Query = "q", TopK = 10 };
        request.Filter.Add(new SearchClause
        {
            Property = "Id", Operator = SearchOperator.Equals,
            Value = new SearchValue { StringVal = "parent-a" }, ClauseType = SearchClauseType.Filter
        });

        var (writer, written) = MakeStream<ChunkSearchResponse>();
        await sut.SearchChunks(request, writer, TestServerCallContext.Create());

        written.Should().ContainSingle();
        written[0].ParentKey.Should().Be("parent-a");
    }
}
