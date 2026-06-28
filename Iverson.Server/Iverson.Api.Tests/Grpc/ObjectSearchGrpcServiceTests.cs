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
using Xunit;

namespace Iverson.Api.Tests.Grpc;

public class ObjectSearchGrpcServiceTests
{
    private readonly IPostgresRepository _sql;
    private readonly SchemaRegistry _registry;
    private readonly IStarRocksRepository _sr;
    private readonly IVectorService _vector;
    private readonly IEmbeddingService _embedding;
    private readonly ObjectSearchGrpcService _sut;

    public ObjectSearchGrpcServiceTests()
    {
        _sql = Substitute.For<IPostgresRepository>();
        _sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);
        _registry  = new SchemaRegistry(_sql, NullLogger<SchemaRegistry>.Instance);
        _sr        = Substitute.For<IStarRocksRepository>();
        _vector    = Substitute.For<IVectorService>();
        _embedding = Substitute.For<IEmbeddingService>();

        _sr.QueryAsync<dynamic>(Arg.Any<string>(), Arg.Any<object?>())
           .Returns(Enumerable.Empty<dynamic>());

        _sut = new ObjectSearchGrpcService(
            _registry, _sr, _vector, _embedding,
            NullLogger<ObjectSearchGrpcService>.Instance);
    }

    private static (IServerStreamWriter<T> writer, List<T> written) MakeStream<T>()
    {
        var written = new List<T>();
        var writer  = Substitute.For<IServerStreamWriter<T>>();
        writer.WriteAsync(Arg.Do<T>(written.Add), Arg.Any<CancellationToken>())
              .Returns(Task.CompletedTask);
        return (writer, written);
    }

    // ── Search ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_ThrowsRpcException_WhenSchemaNotRegistered()
    {
        var (writer, _) = MakeStream<SearchResponse>();
        var act = async () => await _sut.Search(
            new SearchRequest { TypeName = "Ghost" }, writer, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.FailedPrecondition);
    }

    [Fact]
    public async Task Search_CallsStarRocksQueryAsync_AndStreamsResults()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var fakeRow = new Dictionary<string, object> { ["Name"] = "Alice" };
        _sr.QueryAsync<dynamic>(Arg.Any<string>(), Arg.Any<object?>())
           .Returns(new[] { (dynamic)fakeRow }.AsEnumerable());

        var (writer, written) = MakeStream<SearchResponse>();
        await _sut.Search(new SearchRequest { TypeName = "Author" }, writer, TestServerCallContext.Create());

        written.Should().HaveCount(1);
        await _sr.Received(1).QueryAsync<dynamic>(Arg.Any<string>(), Arg.Any<object?>());
    }

    [Fact]
    public async Task Search_SqlContainsTableName()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        string? capturedSql = null;
        _sr.QueryAsync<dynamic>(Arg.Do<string>(s => capturedSql = s), Arg.Any<object?>())
           .Returns(Enumerable.Empty<dynamic>());

        var (writer, _) = MakeStream<SearchResponse>();
        await _sut.Search(new SearchRequest { TypeName = "Author" }, writer, TestServerCallContext.Create());

        capturedSql.Should().Contain("authors");
    }

    [Fact]
    public async Task Search_ContainsClause_ProducesLikeSql()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        string? capturedSql = null;
        _sr.QueryAsync<dynamic>(Arg.Do<string>(s => capturedSql = s), Arg.Any<object?>())
           .Returns(Enumerable.Empty<dynamic>());

        var request = new SearchRequest { TypeName = "Author", Query = new SearchQuery() };
        request.Query.Clauses.Add(new SearchClause
        {
            Property   = "Name",
            Operator   = SearchOperator.Contains,
            Value      = new SearchValue { StringVal = "Alice" },
            ClauseType = SearchClauseType.Filter
        });

        var (writer, _) = MakeStream<SearchResponse>();
        await _sut.Search(request, writer, TestServerCallContext.Create());

        capturedSql.Should().Contain("LIKE");
    }

    // ── Aggregate ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Aggregate_ThrowsRpcException_WhenSchemaNotRegistered()
    {
        var act = async () => await _sut.Aggregate(
            new AggregateRequest { TypeName = "Ghost" }, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.FailedPrecondition);
    }

    [Fact]
    public async Task Aggregate_ThrowsRpcException_WhenNoAggregationsSpecified()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        var act = async () => await _sut.Aggregate(
            new AggregateRequest { TypeName = "Author" }, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task Aggregate_Terms_ReturnsBuckets()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var row1 = new Dictionary<string, object> { ["bucket_key"] = "Alice", ["doc_count"] = 10L };
        var row2 = new Dictionary<string, object> { ["bucket_key"] = "Bob",   ["doc_count"] = 5L  };
        _sr.QueryAsync<dynamic>(Arg.Any<string>(), Arg.Any<object?>())
           .Returns(new[] { (dynamic)row1, (dynamic)row2 }.AsEnumerable());

        var request = new AggregateRequest { TypeName = "Author" };
        request.Aggregations.Add(new AggregationSpec
        {
            Name = "name_terms", Type = AggregationType.Terms, Field = "Name", Size = 5
        });

        var response = await _sut.Aggregate(request, TestServerCallContext.Create());

        response.Results.Should().HaveCount(1);
        response.Results[0].Name.Should().Be("name_terms");
        response.Results[0].Buckets.Should().HaveCount(2);
        response.Results[0].Buckets[0].Key.Should().Be("Alice");
        response.Results[0].Buckets[0].Count.Should().Be(10);
    }

    [Fact]
    public async Task Aggregate_Avg_ReturnsMetricValue()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var metricRow = new Dictionary<string, object> { ["metric_val"] = 42.5 };
        _sr.QueryAsync<dynamic>(Arg.Any<string>(), Arg.Any<object?>())
           .Returns(new[] { (dynamic)metricRow }.AsEnumerable());

        var request = new AggregateRequest { TypeName = "Author" };
        request.Aggregations.Add(new AggregationSpec
        {
            Name = "bio_avg", Type = AggregationType.Avg, Field = "Bio"
        });

        var response = await _sut.Aggregate(request, TestServerCallContext.Create());

        response.Results.Should().HaveCount(1);
        response.Results[0].MetricValue.Should().BeApproximately(42.5, 0.001);
    }

    [Fact]
    public async Task Aggregate_Terms_ResponseTypeRoundTrips()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var row = new Dictionary<string, object> { ["bucket_key"] = "Alice", ["doc_count"] = 3L };
        _sr.QueryAsync<dynamic>(Arg.Any<string>(), Arg.Any<object?>())
           .Returns(new[] { (dynamic)row }.AsEnumerable());

        var request = new AggregateRequest { TypeName = "Author" };
        request.Aggregations.Add(new AggregationSpec
        {
            Name = "name_terms", Type = AggregationType.Terms, Field = "Name", Size = 5
        });

        var response = await _sut.Aggregate(request, TestServerCallContext.Create());

        response.Results.Should().HaveCount(1);
        response.Results[0].Type.Should().Be(AggregationType.Terms);
    }

    [Fact]
    public async Task Aggregate_WithFilterQuery_PropagatesFilterIntoSql()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        string? capturedSql = null;
        _sr.QueryAsync<dynamic>(Arg.Do<string>(s => capturedSql = s), Arg.Any<object?>())
           .Returns(Enumerable.Empty<dynamic>());

        var query = new SearchQuery();
        query.Clauses.Add(new SearchClause
        {
            Property   = "Name",
            Operator   = SearchOperator.Equals,
            Value      = new SearchValue { StringVal = "Alice" },
            ClauseType = SearchClauseType.Filter
        });

        var request = new AggregateRequest { TypeName = "Author", Query = query };
        request.Aggregations.Add(new AggregationSpec
        {
            Name = "name_terms", Type = AggregationType.Terms, Field = "Name", Size = 5
        });

        await _sut.Aggregate(request, TestServerCallContext.Create());

        capturedSql.Should().NotBeNull();
        capturedSql.Should().Contain("WHERE");
        capturedSql.Should().Contain("`Name`");
    }

    [Fact]
    public async Task Aggregate_WithMultipleSpecs_QueriesAllConcurrently()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var callCount = 0;

        _sr.QueryAsync<dynamic>(Arg.Any<string>(), Arg.Any<object?>())
           .Returns(_ =>
           {
               System.Threading.Interlocked.Increment(ref callCount);
               return Task.FromResult(Enumerable.Empty<dynamic>());
           });

        var request = new AggregateRequest
        {
            TypeName = "Author",
            Aggregations =
            {
                new AggregationSpec { Name = "a1", Field = "Name", Type = AggregationType.Terms },
                new AggregationSpec { Name = "a2", Field = "Name", Type = AggregationType.Terms },
                new AggregationSpec { Name = "a3", Field = "Name", Type = AggregationType.Terms }
            }
        };

        var response = await _sut.Aggregate(request, TestServerCallContext.Create());

        callCount.Should().Be(3);
        response.Results.Should().HaveCount(3);
    }

    [Fact]
    public async Task Search_WithFieldsProjection_PassesFieldsToQueryBuilder()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleWithProjectionSchema());

        string? capturedSql = null;
        _sr.QueryAsync<dynamic>(Arg.Do<string>(sql => capturedSql = sql), Arg.Any<object?>())
           .Returns(Enumerable.Empty<dynamic>());

        var req = new SearchRequest
        {
            TypeName = "Article",
            PageSize = 10,
        };
        req.Fields.Add("Category");
        req.Fields.Add("PublishedAt");

        var (writer, _) = MakeStream<SearchResponse>();
        await _sut.Search(req, writer, TestServerCallContext.Create());

        capturedSql.Should().NotBeNull();
        capturedSql!.Should().Contain("`Category`");
        capturedSql!.Should().Contain("`PublishedAt`");
        capturedSql!.Should().NotContain("`Body`");
        capturedSql!.Should().Contain("`Id`");
    }

    // ── SearchSimilar / SearchChunks — Qdrant paths unchanged ─────────────────

    [Fact]
    public async Task SearchSimilar_ThrowsRpcException_WhenSchemaNotRegistered()
    {
        var (writer, _) = MakeStream<SearchResponse>();
        var act = async () => await _sut.SearchSimilar(
            new SearchSimilarRequest { TypeName = "Ghost", Property = "Name", Query = "test" },
            writer, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.FailedPrecondition);
    }

    [Fact]
    public async Task SearchSimilar_ThrowsRpcException_WhenPropertyHasNoVectorAnnotation()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema()); // no vector fields

        var (writer, _) = MakeStream<SearchResponse>();
        var act = async () => await _sut.SearchSimilar(
            new SearchSimilarRequest { TypeName = "Author", Property = "Name", Query = "test" },
            writer, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task SearchSimilar_ThrowsRpcException_WhenNoCollection()
    {
        // Schema has a VectorField but CollectionName is null
        var schema = new SchemaDescriptor
        {
            TypeName       = "VecNoCollection",
            TableName      = "vec_no_collection",
            CollectionName = null,
            KeyColumn      = new ColumnDescriptor("Id", "uuid", false),
            ScalarColumns  = [],
            FkColumns      = [],
            VectorFields   = [new VectorDescriptor("Title", 1536, "text-embedding-3-small")],
            ChunkFields    = [],
            Relations      = []
        };
        await _registry.RegisterAsync(schema);

        var (writer, _) = MakeStream<SearchResponse>();
        var act = async () => await _sut.SearchSimilar(
            new SearchSimilarRequest { TypeName = "VecNoCollection", Property = "Title", Query = "test" },
            writer, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.FailedPrecondition);
    }

    [Fact]
    public async Task SearchSimilar_CallsEmbedThenQdrant_AndStreamsResults()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var fakeVector = new float[768];
        _embedding.EmbedAsync("test query", Arg.Any<CancellationToken>()).Returns(fakeVector);

        var vectorResult = new VectorSearchResult(
            Id: 1, Score: 0.95,
            Payload: new Dictionary<string, string> { ["title"] = "Great Article" });

        _vector.SearchNamedAsync("articles", "title_vector", fakeVector, Arg.Any<ulong>())
               .Returns(new List<VectorSearchResult> { vectorResult }.AsReadOnly());

        var (writer, written) = MakeStream<SearchResponse>();
        await _sut.SearchSimilar(
            new SearchSimilarRequest { TypeName = "Article", Property = "Title", Query = "test query", TopK = 5 },
            writer, TestServerCallContext.Create());

        written.Should().HaveCount(1);
        written[0].Score.Should().BeApproximately(0.95f, 0.001f);
    }

    // ── SearchChunks — Qdrant path unchanged ──────────────────────────────────

    [Fact]
    public async Task SearchChunks_ThrowsRpcException_WhenSchemaNotRegistered()
    {
        var (writer, _) = MakeStream<ChunkSearchResponse>();
        var act = async () => await _sut.SearchChunks(
            new SearchChunksRequest { TypeName = "Ghost", Property = "Body", Query = "test" },
            writer, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.FailedPrecondition);
    }

    [Fact]
    public async Task SearchChunks_ThrowsRpcException_WhenPropertyHasNoChunkAnnotation()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema()); // no chunk fields

        var (writer, _) = MakeStream<ChunkSearchResponse>();
        var act = async () => await _sut.SearchChunks(
            new SearchChunksRequest { TypeName = "Author", Property = "Name", Query = "test" },
            writer, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task SearchChunks_CallsEmbedThenChunksCollection_AndStreamsResults()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var fakeVector = new float[768];
        _embedding.EmbedAsync("test query", Arg.Any<CancellationToken>()).Returns(fakeVector);

        var chunkResult = new VectorSearchResult(
            Id: 42, Score: 0.88,
            Payload: new Dictionary<string, string> { ["text"] = "passage text", ["parent_id"] = "parent-id-123" });

        _vector.SearchNamedAsync("articles_chunks", "body_vector", fakeVector, Arg.Any<ulong>())
               .Returns(new List<VectorSearchResult> { chunkResult }.AsReadOnly());

        var (writer, written) = MakeStream<ChunkSearchResponse>();
        await _sut.SearchChunks(
            new SearchChunksRequest { TypeName = "Article", Property = "Body", Query = "test query", TopK = 5 },
            writer, TestServerCallContext.Create());

        await _vector.Received(1).SearchNamedAsync(
            "articles_chunks", Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<ulong>());
        written.Should().HaveCount(1);
    }

    [Fact]
    public async Task SearchChunks_ReturnsChunkTextFromPayload()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var fakeVector = new float[768];
        _embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(fakeVector);

        var chunkResult = new VectorSearchResult(
            Id: 99, Score: 0.75,
            Payload: new Dictionary<string, string>
            {
                ["text"]      = "passage text",
                ["parent_id"] = "parent-id-123"
            });

        _vector.SearchNamedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<ulong>())
               .Returns(new List<VectorSearchResult> { chunkResult }.AsReadOnly());

        var (writer, written) = MakeStream<ChunkSearchResponse>();
        await _sut.SearchChunks(
            new SearchChunksRequest { TypeName = "Article", Property = "Body", Query = "semantic search", TopK = 5 },
            writer, TestServerCallContext.Create());

        written.Should().HaveCount(1);
        written[0].ChunkText.Should().Be("passage text");
        written[0].ParentKey.Should().Be("parent-id-123");
    }
}
