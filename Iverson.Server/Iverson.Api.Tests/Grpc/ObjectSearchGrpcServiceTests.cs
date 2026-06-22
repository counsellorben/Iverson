using FluentAssertions;
using Grpc.Core;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Iverson.Elasticsearch;
using Iverson.Embeddings;
using Iverson.Sql;
using Iverson.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using EsAggResult = Iverson.Elasticsearch.AggregationResult;
using EsAggSpec   = Iverson.Elasticsearch.AggregationSpec;
using EsAggBucket = Iverson.Elasticsearch.AggregationBucket;
using EsAggKind   = Iverson.Elasticsearch.AggregationKind;
using ProtoAggSpec = Iverson.Client.Contracts.AggregationSpec;

namespace Iverson.Api.Tests.Grpc;

public class ObjectSearchGrpcServiceTests
{
    private readonly IPostgresRepository _sql;
    private readonly SchemaRegistry _registry;
    private readonly IElasticsearchService _es;
    private readonly IVectorService _vector;
    private readonly IEmbeddingService _embedding;
    private readonly ObjectSearchGrpcService _sut;

    public ObjectSearchGrpcServiceTests()
    {
        _sql = Substitute.For<IPostgresRepository>();
        _sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);

        _registry  = new SchemaRegistry(_sql, NullLogger<SchemaRegistry>.Instance);
        _es        = Substitute.For<IElasticsearchService>();
        _vector    = Substitute.For<IVectorService>();
        _embedding = Substitute.For<IEmbeddingService>();

        _sut = new ObjectSearchGrpcService(
            _registry,
            _es,
            _vector,
            _embedding,
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
        var request = new SearchRequest { TypeName = "Ghost" };

        var act = async () => await _sut.Search(request, writer, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.FailedPrecondition);
    }

    [Fact]
    public async Task Search_CallsElasticsearch_AndStreamsResults()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var doc = new Dictionary<string, object?> { ["Name"] = "Alice" };
        _es.SearchAsync<Dictionary<string, object?>>("authors", Arg.Any<string>())
           .Returns(new List<Dictionary<string, object?>> { doc }.AsReadOnly());

        var (writer, written) = MakeStream<SearchResponse>();
        var request = new SearchRequest { TypeName = "Author" };

        await _sut.Search(request, writer, TestServerCallContext.Create());

        written.Should().HaveCount(1);
        await writer.Received(1).WriteAsync(Arg.Any<SearchResponse>(), Arg.Any<CancellationToken>());
    }

    // ── SearchSimilar ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchSimilar_ThrowsRpcException_WhenSchemaNotRegistered()
    {
        var (writer, _) = MakeStream<SearchResponse>();
        var request = new SearchSimilarRequest { TypeName = "Ghost", Property = "Name", Query = "test" };

        var act = async () => await _sut.SearchSimilar(request, writer, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.FailedPrecondition);
    }

    [Fact]
    public async Task SearchSimilar_ThrowsRpcException_WhenPropertyHasNoVectorAnnotation()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema()); // no vector fields

        var (writer, _) = MakeStream<SearchResponse>();
        var request = new SearchSimilarRequest { TypeName = "Author", Property = "Name", Query = "test" };

        var act = async () => await _sut.SearchSimilar(request, writer, TestServerCallContext.Create());

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
            IndexName      = "vec_no_collection",
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
        var request = new SearchSimilarRequest { TypeName = "VecNoCollection", Property = "Title", Query = "test" };

        var act = async () => await _sut.SearchSimilar(request, writer, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.FailedPrecondition);
    }

    [Fact]
    public async Task SearchSimilar_CallsEmbedThenQdrant_AndStreamsResults()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var fakeVector = new float[1536];
        _embedding.EmbedAsync("test query", "text-embedding-3-small", Arg.Any<CancellationToken>())
                  .Returns(fakeVector);

        var vectorResult = new VectorSearchResult(
            Id: 1,
            Score: 0.95,
            Payload: new Dictionary<string, string> { ["title"] = "Great Article" });

        _vector.SearchNamedAsync("articles", "title_vector", fakeVector, Arg.Any<ulong>())
               .Returns(new List<VectorSearchResult> { vectorResult }.AsReadOnly());

        var (writer, written) = MakeStream<SearchResponse>();
        var request = new SearchSimilarRequest
        {
            TypeName = "Article",
            Property = "Title",
            Query    = "test query",
            TopK     = 5
        };

        await _sut.SearchSimilar(request, writer, TestServerCallContext.Create());

        written.Should().HaveCount(1);
        written[0].Score.Should().BeApproximately(0.95f, 0.001f);
    }

    // ── SearchChunks ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchChunks_ThrowsRpcException_WhenSchemaNotRegistered()
    {
        var (writer, _) = MakeStream<ChunkSearchResponse>();
        var request = new SearchChunksRequest { TypeName = "Ghost", Property = "Body", Query = "test" };

        var act = async () => await _sut.SearchChunks(request, writer, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.FailedPrecondition);
    }

    [Fact]
    public async Task SearchChunks_ThrowsRpcException_WhenPropertyHasNoChunkAnnotation()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema()); // no chunk fields

        var (writer, _) = MakeStream<ChunkSearchResponse>();
        var request = new SearchChunksRequest { TypeName = "Author", Property = "Name", Query = "test" };

        var act = async () => await _sut.SearchChunks(request, writer, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task SearchChunks_CallsEmbedThenChunksCollection_AndStreamsResults()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var fakeVector = new float[1536];
        _embedding.EmbedAsync("test query", "text-embedding-3-small", Arg.Any<CancellationToken>())
                  .Returns(fakeVector);

        var chunkResult = new VectorSearchResult(
            Id: 42,
            Score: 0.88,
            Payload: new Dictionary<string, string> { ["text"] = "passage text", ["parent_id"] = "parent-id-123" });

        _vector.SearchNamedAsync("articles_chunks", "body_vector", fakeVector, Arg.Any<ulong>())
               .Returns(new List<VectorSearchResult> { chunkResult }.AsReadOnly());

        var (writer, written) = MakeStream<ChunkSearchResponse>();
        var request = new SearchChunksRequest
        {
            TypeName = "Article",
            Property = "Body",
            Query    = "test query",
            TopK     = 5
        };

        await _sut.SearchChunks(request, writer, TestServerCallContext.Create());

        await _vector.Received(1).SearchNamedAsync(
            "articles_chunks",
            Arg.Any<string>(),
            Arg.Any<float[]>(),
            Arg.Any<ulong>());

        written.Should().HaveCount(1);
    }

    [Fact]
    public async Task SearchChunks_ReturnsChunkTextFromPayload()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var fakeVector = new float[1536];
        _embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                  .Returns(fakeVector);

        var chunkResult = new VectorSearchResult(
            Id: 99,
            Score: 0.75,
            Payload: new Dictionary<string, string>
            {
                ["text"]      = "passage text",
                ["parent_id"] = "parent-id-123"
            });

        _vector.SearchNamedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<ulong>())
               .Returns(new List<VectorSearchResult> { chunkResult }.AsReadOnly());

        var (writer, written) = MakeStream<ChunkSearchResponse>();
        var request = new SearchChunksRequest
        {
            TypeName = "Article",
            Property = "Body",
            Query    = "semantic search",
            TopK     = 5
        };

        await _sut.SearchChunks(request, writer, TestServerCallContext.Create());

        written.Should().HaveCount(1);
        written[0].ChunkText.Should().Be("passage text");
        written[0].ParentKey.Should().Be("parent-id-123");
    }

    // ── BuildQueryText (tested via Search) ───────────────────────────────────

    [Theory]
    [InlineData(SearchOperator.Contains)]
    [InlineData(SearchOperator.Equals)]
    [InlineData(SearchOperator.StartsWith)]
    public async Task Search_IncludesTextClauseValue_InEsQueryString(SearchOperator op)
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _es.SearchAsync<Dictionary<string, object?>>(Arg.Any<string>(), Arg.Any<string>())
           .Returns(new List<Dictionary<string, object?>>().AsReadOnly());

        string? capturedQuery = null;
        _es.SearchAsync<Dictionary<string, object?>>("authors", Arg.Do<string>(q => capturedQuery = q))
           .Returns(new List<Dictionary<string, object?>>().AsReadOnly());

        var (writer, _) = MakeStream<SearchResponse>();
        var request = new SearchRequest { TypeName = "Author" };
        request.Query = new SearchQuery();
        request.Query.Clauses.Add(new SearchClause
        {
            Property   = "Name",
            Operator   = op,
            Value      = new SearchValue { StringVal = "Iverson" },
            ClauseType = SearchClauseType.Filter
        });

        await _sut.Search(request, writer, TestServerCallContext.Create());

        capturedQuery.Should().Contain("Iverson");
    }

    [Theory]
    [InlineData(SearchOperator.GreaterThan)]
    [InlineData(SearchOperator.LessThan)]
    [InlineData(SearchOperator.GreaterThanOrEquals)]
    [InlineData(SearchOperator.LessThanOrEquals)]
    [InlineData(SearchOperator.NotEquals)]
    [InlineData(SearchOperator.In)]
    [InlineData(SearchOperator.VectorSimilar)]
    public async Task Search_ExcludesNonTextClause_FromEsQueryString(SearchOperator op)
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        string? capturedQuery = null;
        _es.SearchAsync<Dictionary<string, object?>>("authors", Arg.Do<string>(q => capturedQuery = q))
           .Returns(new List<Dictionary<string, object?>>().AsReadOnly());

        var (writer, _) = MakeStream<SearchResponse>();
        var request = new SearchRequest { TypeName = "Author" };
        request.Query = new SearchQuery();
        request.Query.Clauses.Add(new SearchClause
        {
            Property   = "Name",
            Operator   = op,
            Value      = new SearchValue { StringVal = "ShouldNotAppear" },
            ClauseType = SearchClauseType.Filter
        });

        await _sut.Search(request, writer, TestServerCallContext.Create());

        capturedQuery.Should().NotContain("ShouldNotAppear");
    }

    [Fact]
    public async Task Search_JoinsMultipleTextClauses_WithSpace()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        string? capturedQuery = null;
        _es.SearchAsync<Dictionary<string, object?>>("authors", Arg.Do<string>(q => capturedQuery = q))
           .Returns(new List<Dictionary<string, object?>>().AsReadOnly());

        var (writer, _) = MakeStream<SearchResponse>();
        var request = new SearchRequest { TypeName = "Author" };
        request.Query = new SearchQuery();
        request.Query.Clauses.Add(new SearchClause
        {
            Property = "Name",  Operator = SearchOperator.Contains,
            Value    = new SearchValue { StringVal = "Allen" }, ClauseType = SearchClauseType.Filter
        });
        request.Query.Clauses.Add(new SearchClause
        {
            Property = "Bio",   Operator = SearchOperator.Contains,
            Value    = new SearchValue { StringVal = "Iverson" }, ClauseType = SearchClauseType.Filter
        });

        await _sut.Search(request, writer, TestServerCallContext.Create());

        capturedQuery.Should().Be("Allen Iverson");
    }

    [Fact]
    public async Task Search_PassesEmptyString_WhenNoClauses()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        string? capturedQuery = null;
        _es.SearchAsync<Dictionary<string, object?>>("authors", Arg.Do<string>(q => capturedQuery = q))
           .Returns(new List<Dictionary<string, object?>>().AsReadOnly());

        var (writer, _) = MakeStream<SearchResponse>();
        await _sut.Search(new SearchRequest { TypeName = "Author" }, writer, TestServerCallContext.Create());

        capturedQuery.Should().BeEmpty();
    }

    // ── Aggregate ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Aggregate_ThrowsRpcException_WhenSchemaNotRegistered()
    {
        var request = new AggregateRequest { TypeName = "Ghost" };

        var act = async () => await _sut.Aggregate(request, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.FailedPrecondition);
    }

    [Fact]
    public async Task Aggregate_ThrowsRpcException_WhenNoAggregationsSpecified()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var request = new AggregateRequest { TypeName = "Author" };

        var act = async () => await _sut.Aggregate(request, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task Aggregate_CallsEsAggregateAsync_WithMappedSpecs()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        _es.AggregateAsync("authors", Arg.Any<string>(), Arg.Any<IReadOnlyList<EsAggSpec>>())
           .Returns(new List<EsAggResult>().AsReadOnly());

        var request = new AggregateRequest { TypeName = "Author" };
        request.Aggregations.Add(new ProtoAggSpec
        {
            Name  = "name_terms",
            Type  = AggregationType.Terms,
            Field = "Name",
            Size  = 5
        });

        await _sut.Aggregate(request, TestServerCallContext.Create());

        await _es.Received(1).AggregateAsync(
            "authors",
            Arg.Any<string>(),
            Arg.Is<IReadOnlyList<Iverson.Elasticsearch.AggregationSpec>>(
                specs => specs.Count == 1 && specs[0].Name == "name_terms" && specs[0].Kind == Iverson.Elasticsearch.AggregationKind.Terms));
    }

    [Fact]
    public async Task Aggregate_ReturnsMappedResults_ForTermsAggregation()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var esResult = new Iverson.Elasticsearch.AggregationResult(
            Name:    "name_terms",
            Kind:    Iverson.Elasticsearch.AggregationKind.Terms,
            Buckets: new List<Iverson.Elasticsearch.AggregationBucket>
            {
                new("Alice", 10),
                new("Bob",   5)
            });

        _es.AggregateAsync("authors", Arg.Any<string>(), Arg.Any<IReadOnlyList<Iverson.Elasticsearch.AggregationSpec>>())
           .Returns(new List<Iverson.Elasticsearch.AggregationResult> { esResult }.AsReadOnly());

        var request = new AggregateRequest { TypeName = "Author" };
        request.Aggregations.Add(new ProtoAggSpec { Name = "name_terms", Type = AggregationType.Terms, Field = "Name" });

        var response = await _sut.Aggregate(request, TestServerCallContext.Create());

        response.Results.Should().HaveCount(1);
        response.Results[0].Name.Should().Be("name_terms");
        response.Results[0].Type.Should().Be(AggregationType.Terms);
        response.Results[0].Buckets.Should().HaveCount(2);
        response.Results[0].Buckets[0].Key.Should().Be("Alice");
        response.Results[0].Buckets[0].DocCount.Should().Be(10);
    }

    [Fact]
    public async Task Aggregate_ReturnsMappedResults_ForMetricAggregation()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var esResult = new Iverson.Elasticsearch.AggregationResult(
            Name:        "bio_avg",
            Kind:        Iverson.Elasticsearch.AggregationKind.Avg,
            MetricValue: 42.5);

        _es.AggregateAsync("authors", Arg.Any<string>(), Arg.Any<IReadOnlyList<Iverson.Elasticsearch.AggregationSpec>>())
           .Returns(new List<Iverson.Elasticsearch.AggregationResult> { esResult }.AsReadOnly());

        var request = new AggregateRequest { TypeName = "Author" };
        request.Aggregations.Add(new ProtoAggSpec { Name = "bio_avg", Type = AggregationType.Avg, Field = "Bio" });

        var response = await _sut.Aggregate(request, TestServerCallContext.Create());

        response.Results.Should().HaveCount(1);
        response.Results[0].MetricValue.Should().BeApproximately(42.5, 0.001);
        response.Results[0].Type.Should().Be(AggregationType.Avg);
    }

    [Fact]
    public async Task Aggregate_PassesFilterQueryToElasticsearch()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        string? capturedQuery = null;
        _es.AggregateAsync("authors", Arg.Do<string>(q => capturedQuery = q), Arg.Any<IReadOnlyList<Iverson.Elasticsearch.AggregationSpec>>())
           .Returns(new List<Iverson.Elasticsearch.AggregationResult>().AsReadOnly());

        var request = new AggregateRequest { TypeName = "Author" };
        request.Query = new SearchQuery();
        request.Query.Clauses.Add(new SearchClause
        {
            Property   = "Name",
            Operator   = SearchOperator.Contains,
            Value      = new SearchValue { StringVal = "Alice" },
            ClauseType = SearchClauseType.Filter
        });
        request.Aggregations.Add(new ProtoAggSpec { Name = "n", Type = AggregationType.Terms, Field = "Name" });

        await _sut.Aggregate(request, TestServerCallContext.Create());

        capturedQuery.Should().Contain("Alice");
    }
}
