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
using Filter        = Qdrant.Client.Grpc.Filter;
using SrAggResult   = Iverson.StarRocks.AggregationResult;
using SrAggBucket   = Iverson.StarRocks.AggregationBucket;

namespace Iverson.Api.Tests.Grpc;

public class ObjectSearchGrpcServiceTests
{
    private readonly IRecordStoreQueryExecutor _sql;
    private readonly SchemaRegistry _registry;
    private readonly IEngagementStoreSearchService _search;
    private readonly IVectorQueryService _vector;
    private readonly IEmbeddingService _embedding;
    private readonly ObjectSearchGrpcService _sut;

    public ObjectSearchGrpcServiceTests()
    {
        _sql = Substitute.For<IRecordStoreQueryExecutor>();
        _sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);
        _registry  = new SchemaRegistry(new SchemaRegistryRepository(_sql), NullLogger<SchemaRegistry>.Instance);
        _search    = Substitute.For<IEngagementStoreSearchService>();
        _vector    = Substitute.For<IVectorQueryService>();
        _embedding = Substitute.For<IEmbeddingService>();

        _search.SearchAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, StarRocksQuerySchema?>?>())
            .Returns(Enumerable.Empty<dynamic>());
        _search.AggregateAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, StarRocksQuerySchema?>?>())
            .Returns((SrAggResult?)null);
        _search.GroupByAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<GroupByRequest>(), Arg.Any<Func<string, StarRocksQuerySchema?>>())
            .Returns(Enumerable.Empty<dynamic>());
        _search.PipelineAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<PipelineRequest>(), Arg.Any<Func<string, StarRocksQuerySchema?>>())
            .Returns(Enumerable.Empty<dynamic>());

        _sut = new ObjectSearchGrpcService(
            _registry, _search, _vector, _embedding,
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
    public async Task Search_CallsSearchService_AndStreamsResults()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var fakeRow = new Dictionary<string, object> { ["Name"] = "Alice" };
        _search.SearchAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, StarRocksQuerySchema?>?>())
            .Returns(new[] { (dynamic)fakeRow }.AsEnumerable());

        var (writer, written) = MakeStream<SearchResponse>();
        await _sut.Search(new SearchRequest { TypeName = "Author" }, writer, TestServerCallContext.Create());

        written.Should().HaveCount(1);
        await _search.Received(1).SearchAsync(
            Arg.Any<StarRocksQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
            Arg.Any<Func<string, StarRocksQuerySchema?>?>());
    }

    [Fact]
    public async Task Search_PassesCorrectTableSchema_ToSearchService()
    {
        // SQL generation itself is now StarRocksQueryBuilder's concern (covered by
        // StarRocksQueryBuilderTests in Iverson.StarRocks.Tests). This test verifies
        // ObjectSearchGrpcService converts the registered SchemaDescriptor to a
        // StarRocksQuerySchema targeting the right table before delegating.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        StarRocksQuerySchema? capturedSchema = null;
        _search.SearchAsync(
                Arg.Do<StarRocksQuerySchema>(s => capturedSchema = s), Arg.Any<SearchQuery?>(), Arg.Any<int>(),
                Arg.Any<int>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, StarRocksQuerySchema?>?>())
            .Returns(Enumerable.Empty<dynamic>());

        var (writer, _) = MakeStream<SearchResponse>();
        await _sut.Search(new SearchRequest { TypeName = "Author" }, writer, TestServerCallContext.Create());

        capturedSchema.Should().NotBeNull();
        capturedSchema!.TableName.Should().Be("authors");
    }

    [Fact]
    public async Task Search_ContainsClause_IsPassedThroughToSearchService()
    {
        // CONTAINS -> LIKE translation is StarRocksQueryBuilder's concern (covered by
        // StarRocksQueryBuilderTests). This test verifies the clause reaches the search
        // service unmodified.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        SearchQuery? capturedQuery = null;
        _search.SearchAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Do<SearchQuery?>(q => capturedQuery = q), Arg.Any<int>(),
                Arg.Any<int>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, StarRocksQuerySchema?>?>())
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

        capturedQuery.Should().NotBeNull();
        capturedQuery!.Clauses.Should().ContainSingle(c =>
            c.Property == "Name" && c.Operator == SearchOperator.Contains);
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
    public async Task Aggregate_TranslatesStarRocksQueryTranslationException_ToInvalidArgument()
    {
        // The multi-key-GROUP-BY guard now lives in StarRocksRepository.AggregateAsync
        // (covered by StarRocksRepositorySearchTests). This test verifies
        // ObjectSearchGrpcService still correctly translates a
        // StarRocksQueryTranslationException raised by the search service into an
        // InvalidArgument RpcException.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        _search.AggregateAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, StarRocksQuerySchema?>?>())
            .Returns<Task<SrAggResult?>>(_ => throw new StarRocksQueryTranslationException(
                "Multi-key GROUP BY (group_by_fields with more than one entry) is not yet supported"));

        var request = new AggregateRequest { TypeName = "Author" };
        request.Aggregations.Add(new AggregationSpec
        {
            Name = "by_name_rating", Type = AggregationType.Terms, Field = "Name",
            GroupByFields = { "Name", "Rating" }
        });

        var act = async () => await _sut.Aggregate(request, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task Aggregate_Terms_ReturnsBuckets()
    {
        // Row-shape-to-AggregationResult decoding now happens inside
        // StarRocksRepository.AggregateAsync (covered by StarRocksRepositorySearchTests
        // and Task 5's integration tests). This test mocks the search service to
        // return the already-decoded AggregationResult directly.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        _search.AggregateAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, StarRocksQuerySchema?>?>())
            .Returns(new SrAggResult("name_terms", AggregationKind.Terms,
                Buckets: [new SrAggBucket("Alice", 10), new SrAggBucket("Bob", 5)]));

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

        _search.AggregateAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, StarRocksQuerySchema?>?>())
            .Returns(new SrAggResult("bio_avg", AggregationKind.Avg, MetricValue: 42.5));

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

        _search.AggregateAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, StarRocksQuerySchema?>?>())
            .Returns(new SrAggResult("name_terms", AggregationKind.Terms,
                Buckets: [new SrAggBucket("Alice", 3)]));

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
    public async Task Aggregate_WithFilterQuery_PassesQueryToSearchService()
    {
        // WHERE-clause SQL generation is StarRocksQueryBuilder's concern (covered by
        // StarRocksQueryBuilderTests). This test verifies the filter query reaches the
        // search service unmodified.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        SearchQuery? capturedQuery = null;
        _search.AggregateAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Do<SearchQuery?>(q => capturedQuery = q),
                Arg.Any<AggregationDescriptor>(), Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, StarRocksQuerySchema?>?>())
            .Returns((SrAggResult?)null);

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

        capturedQuery.Should().NotBeNull();
        capturedQuery!.Clauses.Should().ContainSingle(c =>
            c.Property == "Name" && c.Operator == SearchOperator.Equals);
    }

    [Fact]
    public async Task Aggregate_WithMultipleSpecs_QueriesAllConcurrently()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var callCount = 0;

        _search.AggregateAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, StarRocksQuerySchema?>?>())
            .Returns(_ =>
            {
                System.Threading.Interlocked.Increment(ref callCount);
                return Task.FromResult<SrAggResult?>(
                    new SrAggResult("agg", AggregationKind.Terms));
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
    public async Task Search_WithFieldsProjection_PassesFieldsToSearchService()
    {
        // Column-list SQL generation is StarRocksQueryBuilder's concern (covered by
        // StarRocksQueryBuilderTests). This test verifies the requested field
        // projection reaches the search service unmodified.
        await _registry.RegisterAsync(SchemaFixtures.ArticleWithProjectionSchema());

        IReadOnlyList<string>? capturedFields = null;
        _search.SearchAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Do<IReadOnlyList<string>?>(f => capturedFields = f), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, StarRocksQuerySchema?>?>())
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

        capturedFields.Should().NotBeNull();
        capturedFields!.Should().Contain(["Category", "PublishedAt"]);
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

    [Fact]
    public async Task SearchSimilar_WithFilter_PassesTranslatedFilterToVectorService()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var fakeVector = new float[768];
        _embedding.EmbedAsync("test query", Arg.Any<CancellationToken>()).Returns(fakeVector);
        _vector.SearchNamedAsync("articles", "title_vector", fakeVector, Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(new List<VectorSearchResult>().AsReadOnly());

        var request = new SearchSimilarRequest { TypeName = "Article", Property = "Title", Query = "test query", TopK = 5 };
        request.Filter.Add(new SearchClause
        {
            Property = "AuthorId", Operator = SearchOperator.Equals,
            Value = new SearchValue { StringVal = "00000000-0000-0000-0000-000000000001" },
            ClauseType = SearchClauseType.Filter
        });

        var (writer, _) = MakeStream<SearchResponse>();
        await _sut.SearchSimilar(request, writer, TestServerCallContext.Create());

        // NB: Arg.Do does not fire inside Received() verification on this project's NSubstitute
        // version (see project-test-coverage memory) — use ReceivedCalls()/GetArguments() instead.
        var call = _vector.ReceivedCalls()
            .Should().ContainSingle(c => c.GetMethodInfo().Name == nameof(IVectorQueryService.SearchNamedAsync))
            .Subject;
        var captured = (Filter?)call.GetArguments()[4];
        captured.Should().NotBeNull();
        captured!.Must.Should().ContainSingle(c => c.Field.Key == "authorId");
    }

    [Fact]
    public async Task SearchSimilar_FilterOnUnknownProperty_ThrowsInvalidArgument()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(new float[768]);

        var request = new SearchSimilarRequest { TypeName = "Article", Property = "Title", Query = "q", TopK = 5 };
        request.Filter.Add(new SearchClause
        {
            Property = "Nope", Operator = SearchOperator.Equals,
            Value = new SearchValue { StringVal = "x" }, ClauseType = SearchClauseType.Filter
        });

        var (writer, _) = MakeStream<SearchResponse>();
        var act = async () => await _sut.SearchSimilar(request, writer, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument && e.Status.Detail.Contains("Nope"));
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

    [Fact]
    public async Task SearchChunks_WithPkEqualsFilter_PassesParentIdMatchToVectorService()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(new float[768]);
        _vector.SearchNamedAsync("articles_chunks", "body_vector", Arg.Any<float[]>(), Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(new List<VectorSearchResult>().AsReadOnly());

        var request = new SearchChunksRequest { TypeName = "Article", Property = "Body", Query = "q", TopK = 5 };
        request.Filter.Add(new SearchClause
        {
            Property = "Id", Operator = SearchOperator.Equals,
            Value = new SearchValue { StringVal = "parent-123" }, ClauseType = SearchClauseType.Filter
        });

        var (writer, _) = MakeStream<ChunkSearchResponse>();
        await _sut.SearchChunks(request, writer, TestServerCallContext.Create());

        // NB: Arg.Do does not fire inside Received() verification on this project's NSubstitute
        // version (see project-test-coverage memory) — use ReceivedCalls()/GetArguments() instead.
        var call = _vector.ReceivedCalls()
            .Should().ContainSingle(c => c.GetMethodInfo().Name == nameof(IVectorQueryService.SearchNamedAsync))
            .Subject;
        var captured = (Filter?)call.GetArguments()[4];
        captured.Should().NotBeNull();
        captured!.Must.Should().ContainSingle(c =>
            c.Field.Key == "parent_id" && c.Field.Match.Keyword == "parent-123");
    }

    [Fact]
    public async Task SearchChunks_FilterOnNonPkProperty_ThrowsInvalidArgument()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(new float[768]);

        var request = new SearchChunksRequest { TypeName = "Article", Property = "Body", Query = "q", TopK = 5 };
        request.Filter.Add(new SearchClause
        {
            Property = "AuthorId", Operator = SearchOperator.Equals,
            Value = new SearchValue { StringVal = "x" }, ClauseType = SearchClauseType.Filter
        });

        var (writer, _) = MakeStream<ChunkSearchResponse>();
        var act = async () => await _sut.SearchChunks(request, writer, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task SearchChunks_MoreThanOneFilterClause_ThrowsInvalidArgument()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(new float[768]);

        var request = new SearchChunksRequest { TypeName = "Article", Property = "Body", Query = "q", TopK = 5 };
        request.Filter.Add(new SearchClause { Property = "Id", Operator = SearchOperator.Equals, Value = new SearchValue { StringVal = "a" } });
        request.Filter.Add(new SearchClause { Property = "Id", Operator = SearchOperator.Equals, Value = new SearchValue { StringVal = "b" } });

        var (writer, _) = MakeStream<ChunkSearchResponse>();
        var act = async () => await _sut.SearchChunks(request, writer, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task SearchChunks_NonEqualsOperator_ThrowsInvalidArgument()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(new float[768]);

        var request = new SearchChunksRequest { TypeName = "Article", Property = "Body", Query = "q", TopK = 5 };
        request.Filter.Add(new SearchClause
        {
            Property = "Id", Operator = SearchOperator.NotEquals,
            Value = new SearchValue { StringVal = "parent-123" }, ClauseType = SearchClauseType.Filter
        });

        var (writer, _) = MakeStream<ChunkSearchResponse>();
        var act = async () => await _sut.SearchChunks(request, writer, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument
                     && e.Status.Detail.Contains("MUST_NOT"));
    }

    [Fact]
    public async Task SearchChunks_MustNotClauseType_ThrowsInvalidArgument()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(new float[768]);

        var request = new SearchChunksRequest { TypeName = "Article", Property = "Body", Query = "q", TopK = 5 };
        request.Filter.Add(new SearchClause
        {
            Property = "Id", Operator = SearchOperator.Equals,
            Value = new SearchValue { StringVal = "parent-123" }, ClauseType = SearchClauseType.MustNot
        });

        var (writer, _) = MakeStream<ChunkSearchResponse>();
        var act = async () => await _sut.SearchChunks(request, writer, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument
                     && e.Status.Detail.Contains("MUST_NOT"));
    }

    // ── Pipeline ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pipeline_ThrowsRpcException_WhenSchemaNotRegistered()
    {
        var (writer, _) = MakeStream<SearchResponse>();
        var act = async () => await _sut.Pipeline(
            new PipelineRequest { TypeName = "Ghost" }, writer, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.FailedPrecondition);
    }

    [Fact]
    public async Task Pipeline_PassesStepsToSearchService_AndStreamsRows()
    {
        // CTE SQL generation is StarRocksPipelineBuilder's concern (covered by
        // StarRocksPipelineBuilderTests). This test verifies the pipeline steps reach
        // the search service unmodified and that returned rows are streamed correctly.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        PipelineRequest? capturedRequest = null;
        var fakeRow = new Dictionary<string, object> { ["Name"] = "Alice", ["n"] = 3L };
        _search.PipelineAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Do<PipelineRequest>(r => capturedRequest = r),
                Arg.Any<Func<string, StarRocksQuerySchema?>>())
            .Returns(new[] { (dynamic)fakeRow }.AsEnumerable());

        var step = new PipelineStep { Name = "by_name" };
        step.GroupBy.Add(new GroupKey { Field = "Name" });
        step.Metrics.Add(new MetricSpec { Name = "n", Type = AggregationType.Count });
        var request = new PipelineRequest { TypeName = "Author" };
        request.Steps.Add(step);

        var (writer, written) = MakeStream<SearchResponse>();
        await _sut.Pipeline(request, writer, TestServerCallContext.Create());

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Steps.Should().ContainSingle(s => s.Name == "by_name");
        written.Should().HaveCount(1);
        written[0].Data.Fields["Name"].StringValue.Should().Be("Alice");
    }

    [Fact]
    public async Task Pipeline_TranslatesStarRocksQueryTranslationException_ToInvalidArgument()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        _search.PipelineAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<PipelineRequest>(), Arg.Any<Func<string, StarRocksQuerySchema?>>())
            .Returns<Task<IEnumerable<dynamic>>>(_ => throw new StarRocksQueryTranslationException(
                "step 's' reads from unknown step 'nonexistent'"));

        var step = new PipelineStep { Name = "s", Reads = "nonexistent" };
        var request = new PipelineRequest { TypeName = "Author" };
        request.Steps.Add(step);

        var (writer, _) = MakeStream<SearchResponse>();
        var act = async () => await _sut.Pipeline(request, writer, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task Pipeline_StarRocksNotReady_ThrowsUnavailable()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleWithProjectionSchema());
        _search.PipelineAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<PipelineRequest>(), Arg.Any<Func<string, StarRocksQuerySchema?>>())
            .Returns<Task<IEnumerable<dynamic>>>(_ => throw new StarRocksNotReadyException("warming up"));

        var request = new PipelineRequest { TypeName = "Article" };
        var (writer, _) = MakeStream<SearchResponse>();

        var act = async () => await _sut.Pipeline(request, writer, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.Status.StatusCode == StatusCode.Unavailable);
    }

    [Fact]
    public async Task Pipeline_StreamsResults_PropagatesTraceId()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleWithProjectionSchema());
        _search.PipelineAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<PipelineRequest>(), Arg.Any<Func<string, StarRocksQuerySchema?>>())
            .Returns(new List<dynamic> { new Dictionary<string, object?> { ["Title"] = "T" } });

        var request = new PipelineRequest { TypeName = "Article", TraceId = "trace-xyz" };
        var (writer, written) = MakeStream<SearchResponse>();

        await _sut.Pipeline(request, writer, TestServerCallContext.Create());

        written.Should().ContainSingle(r => r.TraceId == "trace-xyz");
    }
}
