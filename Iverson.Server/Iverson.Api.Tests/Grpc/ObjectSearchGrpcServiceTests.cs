using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Authorization;
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
    private readonly IActingUserAccessor _actingUserAccessor;
    private readonly IRowFieldAuthorizationEvaluator _authEvaluator = new RowFieldAuthorizationEvaluator();
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
                Arg.Any<Func<string, StarRocksQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns(Enumerable.Empty<dynamic>());
        _search.AggregateAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, StarRocksQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns((SrAggResult?)null);
        _search.GroupByAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<GroupByRequest>(), Arg.Any<Func<string, StarRocksQuerySchema?>>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns(Enumerable.Empty<dynamic>());
        _search.PipelineAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<PipelineRequest>(), Arg.Any<Func<string, StarRocksQuerySchema?>>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns(Enumerable.Empty<dynamic>());

        _actingUserAccessor = new ActingUserAccessor
            { ActingUser = ActingUserFixtures.Principal("test-user", "test-bypass") };
        _sut = new ObjectSearchGrpcService(
            _registry, _search, _vector, _embedding,
            NullLogger<ObjectSearchGrpcService>.Instance,
            _actingUserAccessor, _authEvaluator);
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
                Arg.Any<Func<string, StarRocksQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns(new[] { (dynamic)fakeRow }.AsEnumerable());

        var (writer, written) = MakeStream<SearchResponse>();
        await _sut.Search(new SearchRequest { TypeName = "Author" }, writer, TestServerCallContext.Create());

        written.Should().HaveCount(1);
        await _search.Received(1).SearchAsync(
            Arg.Any<StarRocksQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
            Arg.Any<Func<string, StarRocksQuerySchema?>?>(),
            Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>());
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
                Arg.Any<Func<string, StarRocksQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
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
                Arg.Any<Func<string, StarRocksQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
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

    // ── Search — authorization ────────────────────────────────────────────────

    private static SchemaDescriptor OwnedSchema(
        string typeName, string? ownerField, IReadOnlyList<Iverson.Api.Schema.FieldPermission>? fieldPermissions = null,
        string bypassRole = "test-bypass") => new()
    {
        TypeName       = typeName,
        TableName      = typeName.ToLowerInvariant() + "s",
        CollectionName = null,
        KeyColumn      = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns  = [new ColumnDescriptor("Name", "text", false), new ColumnDescriptor("Secret", "text", true)],
        FkColumns      = [],
        VectorFields   = [],
        ChunkFields    = [],
        Relations      = [],
        Authorization  = new Iverson.Api.Schema.AuthorizationRules(
            ownerField,
            new List<Iverson.Api.Schema.RowPermission> { new(bypassRole, true, true, true) },
            fieldPermissions?.ToList() ?? [])
    };

    [Fact]
    public async Task Search_NoAuthorizationRules_ReturnsEmptyStream_WithoutQueryingSearchService()
    {
        var schema = SchemaFixtures.AuthorSchema() with { Authorization = null };
        await _registry.RegisterAsync(schema);

        var (writer, written) = MakeStream<SearchResponse>();
        await _sut.Search(new SearchRequest { TypeName = "Author" }, writer, TestServerCallContext.Create());

        written.Should().BeEmpty();
        _search.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Search_NoActingUser_ReturnsEmptyStream_WithoutQueryingSearchService()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _actingUserAccessor.ActingUser = null;

        var (writer, written) = MakeStream<SearchResponse>();
        await _sut.Search(new SearchRequest { TypeName = "Author" }, writer, TestServerCallContext.Create());

        written.Should().BeEmpty();
        _search.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Search_BypassCaller_ForwardsUnrestrictedConstraint_NoOwnerFilter()
    {
        // Caller is in the row-permission's bypass role, even though the schema declares an
        // OwnerField — bypass must short-circuit ownership, so the forwarded constraint carries
        // a null OwnerColumn (i.e. "sees all rows", no WHERE-clause owner predicate added).
        await _registry.RegisterAsync(OwnedSchema("Owned", "OwnerId"));

        IReadOnlyDictionary<string, AuthorizationConstraint>? captured = null;
        _search.SearchAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, StarRocksQuerySchema?>?>(),
                Arg.Do<IReadOnlyDictionary<string, AuthorizationConstraint>?>(a => captured = a))
            .Returns(Enumerable.Empty<dynamic>());

        var (writer, _) = MakeStream<SearchResponse>();
        await _sut.Search(new SearchRequest { TypeName = "Owned" }, writer, TestServerCallContext.Create());

        captured.Should().NotBeNull();
        captured!["Owned"].OwnerColumn.Should().BeNull();
        captured["Owned"].AllowedFields.Should().BeNull();
    }

    [Fact]
    public async Task Search_OwnerRestrictedCaller_ForwardsOwnerColumnAndCallerIdAsOwnerValue()
    {
        // Caller is NOT in the bypass role, and the schema requires ownership — the forwarded
        // constraint must carry the owner column and the caller's own identity as the value that
        // BuildSearch/BuildFromWithJoins will use to filter rows.
        await _registry.RegisterAsync(OwnedSchema("Owned", "OwnerId"));
        _actingUserAccessor.ActingUser = ActingUserFixtures.Principal("alice", "member");

        IReadOnlyDictionary<string, AuthorizationConstraint>? captured = null;
        _search.SearchAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, StarRocksQuerySchema?>?>(),
                Arg.Do<IReadOnlyDictionary<string, AuthorizationConstraint>?>(a => captured = a))
            .Returns(Enumerable.Empty<dynamic>());

        var (writer, _) = MakeStream<SearchResponse>();
        await _sut.Search(new SearchRequest { TypeName = "Owned" }, writer, TestServerCallContext.Create());

        captured.Should().NotBeNull();
        captured!["Owned"].OwnerColumn.Should().Be("OwnerId");
        captured["Owned"].OwnerValue.Should().Be("alice");
    }

    [Fact]
    public async Task Search_RestrictedFields_MasksDisallowedFieldFromResponse()
    {
        var fieldPermissions = new List<Iverson.Api.Schema.FieldPermission> { new("Secret", ["admin"], []) };
        await _registry.RegisterAsync(OwnedSchema("Owned", "OwnerId", fieldPermissions));
        _actingUserAccessor.ActingUser = ActingUserFixtures.Principal("alice", "member"); // not "admin", not bypass

        var fakeRow = new Dictionary<string, object> { ["Id"] = "1", ["Name"] = "visible", ["Secret"] = "hidden" };
        _search.SearchAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, StarRocksQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns(new[] { (dynamic)fakeRow }.AsEnumerable());

        var (writer, written) = MakeStream<SearchResponse>();
        await _sut.Search(new SearchRequest { TypeName = "Owned" }, writer, TestServerCallContext.Create());

        written.Should().HaveCount(1);
        written[0].Data.Fields.Should().ContainKey("Name");
        written[0].Data.Fields.Should().ContainKey("Id");
        written[0].Data.Fields.Should().NotContainKey("Secret");
    }

    [Fact]
    public async Task Search_JoinedTypeWithNoAuthorizationRules_ThrowsInvalidArgument_WithoutQueryingSearchService()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema() with { Authorization = null });

        var request = new SearchRequest { TypeName = "Author" };
        request.Joins.Add(new JoinSpec
        {
            LeftType = "Author", RightType = "Article", LeftField = "Id", RightField = "AuthorId", Kind = JoinKind.Inner
        });

        var (writer, _) = MakeStream<SearchResponse>();
        var act = async () => await _sut.Search(request, writer, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument && e.Status.Detail.Contains("Article"));
        _search.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Search_JoinedTypeOwnerRestricted_ForwardsOwnerConstraintForJoinedType()
    {
        // Primary type's rules bypass the default caller; the joined type's rules do not (different
        // bypass role) — the caller's own identity must still flow into the joined type's constraint
        // so BuildFromWithJoins can append the ownership condition to that JOIN's ON clause.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema()); // bypass role "test-bypass"
        await _registry.RegisterAsync(OwnedSchema("Article", "OwnerId", bypassRole: "other-bypass"));

        IReadOnlyDictionary<string, AuthorizationConstraint>? captured = null;
        _search.SearchAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, StarRocksQuerySchema?>?>(),
                Arg.Do<IReadOnlyDictionary<string, AuthorizationConstraint>?>(a => captured = a))
            .Returns(Enumerable.Empty<dynamic>());

        var request = new SearchRequest { TypeName = "Author" };
        request.Joins.Add(new JoinSpec
        {
            LeftType = "Author", RightType = "Article", LeftField = "Id", RightField = "AuthorId", Kind = JoinKind.Left
        });

        var (writer, _) = MakeStream<SearchResponse>();
        await _sut.Search(request, writer, TestServerCallContext.Create());

        captured.Should().NotBeNull();
        captured!["Article"].OwnerColumn.Should().Be("OwnerId");
        captured["Article"].OwnerValue.Should().Be("test-user"); // default fixture's sub claim
    }

    [Fact]
    public async Task Search_JoinedTypeOwnerRestricted_CaseInsensitiveJoinTypeName_StillForwardsOwnerConstraint()
    {
        // Regression test for a whole-branch review finding: EvaluateAuthorization's constraints
        // dictionary used to be keyed with the default case-sensitive comparer, and joined-type
        // keys come from the raw request-supplied JoinSpec.LeftType/RightType string. Meanwhile
        // every downstream StarRocks builder (StarRocksQueryBuilder.IsFieldAllowed,
        // StarRocksPipelineBuilder.ResolveJoinSources/EmitStep) looks constraints up by the
        // *canonical* SchemaRegistry TypeName, and SchemaRegistry.Get resolves case-insensitively.
        // So a caller could send a join naming the type differently-cased than its canonical
        // registration (e.g. "article" instead of "Article") — the join would still resolve and
        // execute, but the case-sensitive constraints lookup would silently miss, bypassing
        // ownership/field restriction entirely for that joined type. This test proves the
        // constraint the RPC layer forwards is reachable under the canonical casing even when the
        // request supplied a differently-cased join type name.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema()); // bypass role "test-bypass"
        await _registry.RegisterAsync(OwnedSchema("Article", "OwnerId", bypassRole: "other-bypass"));

        IReadOnlyDictionary<string, AuthorizationConstraint>? captured = null;
        _search.SearchAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, StarRocksQuerySchema?>?>(),
                Arg.Do<IReadOnlyDictionary<string, AuthorizationConstraint>?>(a => captured = a))
            .Returns(Enumerable.Empty<dynamic>());

        var request = new SearchRequest { TypeName = "Author" };
        request.Joins.Add(new JoinSpec
        {
            // Registered canonically as "Article"; the request supplies different casing.
            LeftType = "Author", RightType = "article", LeftField = "Id", RightField = "AuthorId", Kind = JoinKind.Left
        });

        var (writer, _) = MakeStream<SearchResponse>();
        await _sut.Search(request, writer, TestServerCallContext.Create());

        captured.Should().NotBeNull();
        // Looked up under the canonical casing, exactly as StarRocksQueryBuilder/StarRocksPipelineBuilder do.
        captured!["Article"].OwnerColumn.Should().Be("OwnerId");
        captured["Article"].OwnerValue.Should().Be("test-user"); // default fixture's sub claim
    }

    [Fact]
    public async Task Search_LeftJoin_JoinedTypeOwnerRestricted_NonMatchingSideNullsOut_RowNotDropped()
    {
        // Simulates what a correctly-generated LEFT JOIN + ON-clause-appended owner predicate
        // (StarRocksQueryBuilderTests' BuildFromWithJoins_LeftJoin_* SQL-shape tests) actually
        // produces at execution time: every primary-side row survives regardless of whether the
        // caller is authorized to see a matching Article row, because the owner check lives in
        // the JOIN's ON clause rather than the outer WHERE. Had it been placed in WHERE instead,
        // the LEFT JOIN would have silently degraded to INNER JOIN behavior and row 2 below (no
        // authorized Article match) would have been dropped entirely instead of surfacing with
        // its joined-side column null. This test proves the RPC layer forwards such a result set
        // unchanged — no extra row-dropping or error on the null side.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema()); // bypass role "test-bypass"
        await _registry.RegisterAsync(OwnedSchema("Article", "OwnerId", bypassRole: "other-bypass"));

        var ownerMatchRow    = new Dictionary<string, object?> { ["Id"] = "1", ["Name"] = "Alice", ["Title"] = "Owned Article" };
        var nonMatchRow      = new Dictionary<string, object?> { ["Id"] = "2", ["Name"] = "Bob", ["Title"] = null };
        _search.SearchAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, StarRocksQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns(new[] { (dynamic)ownerMatchRow, (dynamic)nonMatchRow }.AsEnumerable());

        var request = new SearchRequest { TypeName = "Author" };
        request.Joins.Add(new JoinSpec
        {
            LeftType = "Author", RightType = "Article", LeftField = "Id", RightField = "AuthorId", Kind = JoinKind.Left
        });

        var (writer, written) = MakeStream<SearchResponse>();
        await _sut.Search(request, writer, TestServerCallContext.Create());

        // Row count preserved: the non-owned-Article row is NOT dropped.
        written.Should().HaveCount(2);

        var alice = written.Single(r => r.Data.Fields["Name"].StringValue == "Alice");
        alice.Data.Fields["Title"].StringValue.Should().Be("Owned Article");

        var bob = written.Single(r => r.Data.Fields["Name"].StringValue == "Bob");
        bob.Data.Fields.Should().ContainKey("Title");
        bob.Data.Fields["Title"].KindCase.Should().Be(Value.KindOneofCase.NullValue);
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
                Arg.Any<Func<string, StarRocksQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
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
                Arg.Any<Func<string, StarRocksQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
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
                Arg.Any<Func<string, StarRocksQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
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
                Arg.Any<Func<string, StarRocksQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
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
                Arg.Any<Func<string, StarRocksQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
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
                Arg.Any<Func<string, StarRocksQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
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

    // ── Aggregate — authorization ───────────────────────────────────────────────
    //
    // Reject-on-reference for spec.Field/GroupByFields/Expression is StarRocksQueryBuilder's
    // concern (covered end-to-end, with real thrown exceptions, by StarRocksQueryBuilderTests'
    // "BuildAggregate — field reject-on-reference" section — including the Expression-tokenizer
    // bypass-closure case). Since `_search` is a mock here, these RPC-level tests instead cover
    // what actually lives in ObjectSearchGrpcService.Aggregate: denied-caller short-circuiting,
    // InvalidArgument translation, and that the correct AuthorizationConstraint (the input
    // BuildAggregate's ownership/field checks act on) is computed and forwarded.

    [Fact]
    public async Task Aggregate_NoAuthorizationRules_ReturnsEmptyResults_WithoutQueryingSearchService()
    {
        var schema = SchemaFixtures.AuthorSchema() with { Authorization = null };
        await _registry.RegisterAsync(schema);

        var request = new AggregateRequest { TypeName = "Author" };
        request.Aggregations.Add(new AggregationSpec { Name = "by_name", Type = AggregationType.Terms, Field = "Name" });

        var response = await _sut.Aggregate(request, TestServerCallContext.Create());

        response.Results.Should().BeEmpty();
        _search.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Aggregate_NoActingUser_ReturnsEmptyResults_WithoutQueryingSearchService()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _actingUserAccessor.ActingUser = null;

        var request = new AggregateRequest { TypeName = "Author" };
        request.Aggregations.Add(new AggregationSpec { Name = "by_name", Type = AggregationType.Terms, Field = "Name" });

        var response = await _sut.Aggregate(request, TestServerCallContext.Create());

        response.Results.Should().BeEmpty();
        _search.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Aggregate_JoinedTypeWithNoAuthorizationRules_ThrowsInvalidArgument_WithoutQueryingSearchService()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema() with { Authorization = null });

        var request = new AggregateRequest { TypeName = "Author" };
        request.Joins.Add(new JoinSpec
        {
            LeftType = "Author", RightType = "Article", LeftField = "Id", RightField = "AuthorId", Kind = JoinKind.Inner
        });
        request.Aggregations.Add(new AggregationSpec { Name = "by_name", Type = AggregationType.Terms, Field = "Name" });

        var act = async () => await _sut.Aggregate(request, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument && e.Status.Detail.Contains("Article"));
        _search.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Aggregate_BypassCaller_ForwardsUnrestrictedConstraint_NoOwnerFilter()
    {
        await _registry.RegisterAsync(OwnedSchema("Owned", "OwnerId"));

        IReadOnlyDictionary<string, AuthorizationConstraint>? captured = null;
        _search.AggregateAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, StarRocksQuerySchema?>?>(),
                Arg.Do<IReadOnlyDictionary<string, AuthorizationConstraint>?>(a => captured = a))
            .Returns((SrAggResult?)null);

        var request = new AggregateRequest { TypeName = "Owned" };
        request.Aggregations.Add(new AggregationSpec { Name = "by_name", Type = AggregationType.Terms, Field = "Name" });

        await _sut.Aggregate(request, TestServerCallContext.Create());

        captured.Should().NotBeNull();
        captured!["Owned"].OwnerColumn.Should().BeNull();
        captured["Owned"].AllowedFields.Should().BeNull();
    }

    [Fact]
    public async Task Aggregate_OwnerRestrictedCaller_ForwardsOwnerColumnAndCallerIdAsOwnerValue()
    {
        // This is the input that makes BuildAggregate's primary-ownership wrap-and-AND (covered
        // in StarRocksQueryBuilderTests) actually filter rows for this caller: proves the RPC
        // computes and forwards it correctly.
        await _registry.RegisterAsync(OwnedSchema("Owned", "OwnerId"));
        _actingUserAccessor.ActingUser = ActingUserFixtures.Principal("alice", "member");

        IReadOnlyDictionary<string, AuthorizationConstraint>? captured = null;
        _search.AggregateAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, StarRocksQuerySchema?>?>(),
                Arg.Do<IReadOnlyDictionary<string, AuthorizationConstraint>?>(a => captured = a))
            .Returns((SrAggResult?)null);

        var request = new AggregateRequest { TypeName = "Owned" };
        request.Aggregations.Add(new AggregationSpec { Name = "by_name", Type = AggregationType.Terms, Field = "Name" });

        await _sut.Aggregate(request, TestServerCallContext.Create());

        captured.Should().NotBeNull();
        captured!["Owned"].OwnerColumn.Should().Be("OwnerId");
        captured["Owned"].OwnerValue.Should().Be("alice");
    }

    [Fact]
    public async Task Aggregate_JoinedTypeOwnerRestricted_ForwardsOwnerConstraintForJoinedType()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema()); // bypass role "test-bypass"
        await _registry.RegisterAsync(OwnedSchema("Article", "OwnerId", bypassRole: "other-bypass"));

        IReadOnlyDictionary<string, AuthorizationConstraint>? captured = null;
        _search.AggregateAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, StarRocksQuerySchema?>?>(),
                Arg.Do<IReadOnlyDictionary<string, AuthorizationConstraint>?>(a => captured = a))
            .Returns((SrAggResult?)null);

        var request = new AggregateRequest { TypeName = "Author" };
        request.Joins.Add(new JoinSpec
        {
            LeftType = "Author", RightType = "Article", LeftField = "Id", RightField = "AuthorId", Kind = JoinKind.Left
        });
        request.Aggregations.Add(new AggregationSpec { Name = "by_name", Type = AggregationType.Terms, Field = "Name" });

        await _sut.Aggregate(request, TestServerCallContext.Create());

        captured.Should().NotBeNull();
        captured!["Article"].OwnerColumn.Should().Be("OwnerId");
        captured["Article"].OwnerValue.Should().Be("test-user"); // default fixture's sub claim
    }

    [Fact]
    public async Task Aggregate_RestrictedFieldRejectedByBuilder_TranslatesToInvalidArgument()
    {
        // Simulates what BuildAggregate (StarRocksQueryTranslationException on a disallowed
        // spec.Field — see StarRocksQueryBuilderTests) causes the real search service to throw;
        // proves ObjectSearchGrpcService.Aggregate still surfaces it as InvalidArgument even
        // though authorization is now evaluated before dispatch.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        _search.AggregateAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, StarRocksQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns<Task<SrAggResult?>>(_ => throw new StarRocksQueryTranslationException(
                "Aggregation field 'Bio' on 'Author' is not authorized for this caller."));

        var request = new AggregateRequest { TypeName = "Author" };
        request.Aggregations.Add(new AggregationSpec { Name = "by_bio", Type = AggregationType.Terms, Field = "Bio" });

        var act = async () => await _sut.Aggregate(request, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument && e.Status.Detail.Contains("Bio"));
    }

    [Fact]
    public async Task Aggregate_RestrictedExpressionFieldRejectedByBuilder_TranslatesToInvalidArgument()
    {
        // Same as above but for the spec.Expression path — proves the bypass (routing a
        // disallowed column through Expression instead of Field) is closed all the way up
        // through the RPC layer, not just at StarRocksQueryBuilder's unit level.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        _search.AggregateAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, StarRocksQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns<Task<SrAggResult?>>(_ => throw new StarRocksQueryTranslationException(
                "Aggregation expression references field 'Bio' on 'Author', which is not authorized for this caller."));

        var request = new AggregateRequest { TypeName = "Author" };
        request.Aggregations.Add(new AggregationSpec
        {
            Name = "revenue", Type = AggregationType.Sum, Field = "Rating", Expression = "Bio * 2"
        });

        var act = async () => await _sut.Aggregate(request, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument && e.Status.Detail.Contains("Bio"));
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
                Arg.Any<Func<string, StarRocksQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
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

    // ── GroupBy — authorization ─────────────────────────────────────────────────
    //
    // Reject-on-reference for Keys/MetricSpec.Field/MetricSpec.Expression (including the
    // Field-vs-Expression independent-check regression) is StarRocksQueryBuilder's concern
    // (covered end-to-end, with real thrown exceptions, by StarRocksQueryBuilderTests'
    // "BuildGroupBy — ownership + field reject-on-reference" section). Since `_search` is a
    // mock here, these RPC-level tests instead cover what actually lives in
    // ObjectSearchGrpcService.GroupBy: denied-caller short-circuiting, InvalidArgument
    // translation, and that the correct AuthorizationConstraint (the input BuildGroupBy's
    // ownership/field checks act on) is computed and forwarded.

    [Fact]
    public async Task GroupBy_NoAuthorizationRules_ReturnsEmptyStream_WithoutQueryingSearchService()
    {
        var schema = SchemaFixtures.AuthorSchema() with { Authorization = null };
        await _registry.RegisterAsync(schema);

        var request = new GroupByRequest { TypeName = "Author", Keys = { "Name" } };
        request.Metrics.Add(new MetricSpec { Name = "cnt", Type = AggregationType.Count });

        var (writer, written) = MakeStream<SearchResponse>();
        await _sut.GroupBy(request, writer, TestServerCallContext.Create());

        written.Should().BeEmpty();
        _search.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task GroupBy_NoActingUser_ReturnsEmptyStream_WithoutQueryingSearchService()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _actingUserAccessor.ActingUser = null;

        var request = new GroupByRequest { TypeName = "Author", Keys = { "Name" } };
        request.Metrics.Add(new MetricSpec { Name = "cnt", Type = AggregationType.Count });

        var (writer, written) = MakeStream<SearchResponse>();
        await _sut.GroupBy(request, writer, TestServerCallContext.Create());

        written.Should().BeEmpty();
        _search.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task GroupBy_JoinedTypeWithNoAuthorizationRules_ThrowsInvalidArgument_WithoutQueryingSearchService()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema() with { Authorization = null });

        var request = new GroupByRequest { TypeName = "Author", Keys = { "Name" } };
        request.Joins.Add(new JoinSpec
        {
            LeftType = "Author", RightType = "Article", LeftField = "Id", RightField = "AuthorId", Kind = JoinKind.Inner
        });
        request.Metrics.Add(new MetricSpec { Name = "cnt", Type = AggregationType.Count });

        var (writer, _) = MakeStream<SearchResponse>();
        var act = async () => await _sut.GroupBy(request, writer, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument && e.Status.Detail.Contains("Article"));
        _search.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task GroupBy_BypassCaller_ForwardsUnrestrictedConstraint_NoOwnerFilter()
    {
        await _registry.RegisterAsync(OwnedSchema("Owned", "OwnerId"));

        IReadOnlyDictionary<string, AuthorizationConstraint>? captured = null;
        _search.GroupByAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<GroupByRequest>(), Arg.Any<Func<string, StarRocksQuerySchema?>>(),
                Arg.Do<IReadOnlyDictionary<string, AuthorizationConstraint>?>(a => captured = a))
            .Returns(Enumerable.Empty<dynamic>());

        var request = new GroupByRequest { TypeName = "Owned", Keys = { "Name" } };
        request.Metrics.Add(new MetricSpec { Name = "cnt", Type = AggregationType.Count });

        var (writer, _) = MakeStream<SearchResponse>();
        await _sut.GroupBy(request, writer, TestServerCallContext.Create());

        captured.Should().NotBeNull();
        captured!["Owned"].OwnerColumn.Should().BeNull();
        captured["Owned"].AllowedFields.Should().BeNull();
    }

    [Fact]
    public async Task GroupBy_OwnerRestrictedCaller_ForwardsOwnerColumnAndCallerIdAsOwnerValue()
    {
        // This is the input that makes BuildGroupBy's primary-ownership wrap-and-AND (covered
        // in StarRocksQueryBuilderTests) actually filter rows for this caller: proves the RPC
        // computes and forwards it correctly.
        await _registry.RegisterAsync(OwnedSchema("Owned", "OwnerId"));
        _actingUserAccessor.ActingUser = ActingUserFixtures.Principal("alice", "member");

        IReadOnlyDictionary<string, AuthorizationConstraint>? captured = null;
        _search.GroupByAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<GroupByRequest>(), Arg.Any<Func<string, StarRocksQuerySchema?>>(),
                Arg.Do<IReadOnlyDictionary<string, AuthorizationConstraint>?>(a => captured = a))
            .Returns(Enumerable.Empty<dynamic>());

        var request = new GroupByRequest { TypeName = "Owned", Keys = { "Name" } };
        request.Metrics.Add(new MetricSpec { Name = "cnt", Type = AggregationType.Count });

        var (writer, _) = MakeStream<SearchResponse>();
        await _sut.GroupBy(request, writer, TestServerCallContext.Create());

        captured.Should().NotBeNull();
        captured!["Owned"].OwnerColumn.Should().Be("OwnerId");
        captured["Owned"].OwnerValue.Should().Be("alice");
    }

    [Fact]
    public async Task GroupBy_JoinedTypeOwnerRestricted_ForwardsOwnerConstraintForJoinedType()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema()); // bypass role "test-bypass"
        await _registry.RegisterAsync(OwnedSchema("Article", "OwnerId", bypassRole: "other-bypass"));

        IReadOnlyDictionary<string, AuthorizationConstraint>? captured = null;
        _search.GroupByAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<GroupByRequest>(), Arg.Any<Func<string, StarRocksQuerySchema?>>(),
                Arg.Do<IReadOnlyDictionary<string, AuthorizationConstraint>?>(a => captured = a))
            .Returns(Enumerable.Empty<dynamic>());

        var request = new GroupByRequest { TypeName = "Author", Keys = { "Name" } };
        request.Joins.Add(new JoinSpec
        {
            LeftType = "Author", RightType = "Article", LeftField = "Id", RightField = "AuthorId", Kind = JoinKind.Left
        });
        request.Metrics.Add(new MetricSpec { Name = "cnt", Type = AggregationType.Count });

        var (writer, _) = MakeStream<SearchResponse>();
        await _sut.GroupBy(request, writer, TestServerCallContext.Create());

        captured.Should().NotBeNull();
        captured!["Article"].OwnerColumn.Should().Be("OwnerId");
        captured["Article"].OwnerValue.Should().Be("test-user"); // default fixture's sub claim
    }

    [Fact]
    public async Task GroupBy_RestrictedKeyRejectedByBuilder_TranslatesToInvalidArgument()
    {
        // Simulates what BuildGroupBy (StarRocksQueryTranslationException on a disallowed
        // request.Keys entry — see StarRocksQueryBuilderTests) causes the real search service
        // to throw; proves ObjectSearchGrpcService.GroupBy still surfaces it as InvalidArgument
        // even though authorization is now evaluated before dispatch.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        _search.GroupByAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<GroupByRequest>(), Arg.Any<Func<string, StarRocksQuerySchema?>>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns<Task<IEnumerable<dynamic>>>(_ => throw new StarRocksQueryTranslationException(
                "GROUP BY key 'Bio' on 'Author' is not authorized for this caller."));

        var request = new GroupByRequest { TypeName = "Author", Keys = { "Bio" } };
        request.Metrics.Add(new MetricSpec { Name = "cnt", Type = AggregationType.Count });

        var (writer, _) = MakeStream<SearchResponse>();
        var act = async () => await _sut.GroupBy(request, writer, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument && e.Status.Detail.Contains("Bio"));
    }

    [Fact]
    public async Task GroupBy_RestrictedMetricFieldRejectedByBuilder_TranslatesToInvalidArgument()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        _search.GroupByAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<GroupByRequest>(), Arg.Any<Func<string, StarRocksQuerySchema?>>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns<Task<IEnumerable<dynamic>>>(_ => throw new StarRocksQueryTranslationException(
                "Field 'Bio' on 'Author' referenced by metric 'by_bio' is not authorized for this caller."));

        var request = new GroupByRequest { TypeName = "Author", Keys = { "Name" } };
        request.Metrics.Add(new MetricSpec { Name = "by_bio", Type = AggregationType.Max, Field = "Bio" });

        var (writer, _) = MakeStream<SearchResponse>();
        var act = async () => await _sut.GroupBy(request, writer, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument && e.Status.Detail.Contains("Bio"));
    }

    [Fact]
    public async Task GroupBy_RestrictedMetricExpressionRejectedByBuilder_TranslatesToInvalidArgument()
    {
        // Same as above but for the metric.Expression path — proves the bypass (routing a
        // disallowed column through Expression instead of Field) is closed all the way up
        // through the RPC layer, not just at StarRocksQueryBuilder's unit level.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        _search.GroupByAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<GroupByRequest>(), Arg.Any<Func<string, StarRocksQuerySchema?>>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns<Task<IEnumerable<dynamic>>>(_ => throw new StarRocksQueryTranslationException(
                "Field 'Bio' on 'Author' referenced by metric 'revenue' expression is not authorized for this caller."));

        var request = new GroupByRequest { TypeName = "Author", Keys = { "Name" } };
        request.Metrics.Add(new MetricSpec { Name = "revenue", Type = AggregationType.Sum, Expression = "Bio * 2" });

        var (writer, _) = MakeStream<SearchResponse>();
        var act = async () => await _sut.GroupBy(request, writer, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument && e.Status.Detail.Contains("Bio"));
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
                Arg.Any<Func<string, StarRocksQuerySchema?>>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
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
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<PipelineRequest>(), Arg.Any<Func<string, StarRocksQuerySchema?>>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
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
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<PipelineRequest>(), Arg.Any<Func<string, StarRocksQuerySchema?>>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
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
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<PipelineRequest>(), Arg.Any<Func<string, StarRocksQuerySchema?>>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns(new List<dynamic> { new Dictionary<string, object?> { ["Title"] = "T" } });

        var request = new PipelineRequest { TypeName = "Article", TraceId = "trace-xyz" };
        var (writer, written) = MakeStream<SearchResponse>();

        await _sut.Pipeline(request, writer, TestServerCallContext.Create());

        written.Should().ContainSingle(r => r.TraceId == "trace-xyz");
    }

    // ── Pipeline — authorization ─────────────────────────────────────────────────
    //
    // Column-introduction filtering / "all: true" scoping / MetricSpec.Expression reject-on-
    // reference / ownership wrap-and-AND (baseWhere + per-join ON) / Layer 2 masking are all
    // StarRocksPipelineBuilder's (and StarRocksRepository's) concern — covered end-to-end, with
    // real thrown exceptions and real generated SQL, by StarRocksPipelineBuilderTests. Since
    // `_search` is a mock here, these RPC-level tests instead cover what actually lives in
    // ObjectSearchGrpcService.Pipeline: denied-caller short-circuiting, InvalidArgument
    // translation, that a PipelineJoin.Source is only evaluated for authorization when it
    // resolves to a registered type (never a prior step name), and that the correct
    // AuthorizationConstraint map is computed and forwarded.

    [Fact]
    public async Task Pipeline_NoAuthorizationRules_ReturnsEmptyStream_WithoutQueryingSearchService()
    {
        var schema = SchemaFixtures.ArticleWithProjectionSchema() with { Authorization = null };
        await _registry.RegisterAsync(schema);

        var request = new PipelineRequest { TypeName = "Article" };
        var (writer, written) = MakeStream<SearchResponse>();
        await _sut.Pipeline(request, writer, TestServerCallContext.Create());

        written.Should().BeEmpty();
        _search.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Pipeline_NoActingUser_ReturnsEmptyStream_WithoutQueryingSearchService()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleWithProjectionSchema());
        _actingUserAccessor.ActingUser = null;

        var request = new PipelineRequest { TypeName = "Article" };
        var (writer, written) = MakeStream<SearchResponse>();
        await _sut.Pipeline(request, writer, TestServerCallContext.Create());

        written.Should().BeEmpty();
        _search.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Pipeline_JoinedTypeWithNoAuthorizationRules_ThrowsInvalidArgument_WithoutQueryingSearchService()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema() with { Authorization = null });

        var step = new PipelineStep { Name = "j" };
        var join = new PipelineJoin { Source = "Article", Kind = JoinKind.Inner };
        join.On.Add(new JoinCondition { Left = "Id", Right = "AuthorId" });
        step.Joins.Add(join);
        step.Select.Add(new SelectItem { All = true });

        var request = new PipelineRequest { TypeName = "Author" };
        request.Steps.Add(step);

        var (writer, _) = MakeStream<SearchResponse>();
        var act = async () => await _sut.Pipeline(request, writer, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument && e.Status.Detail.Contains("Article"));
        _search.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Pipeline_JoinSourceIsPriorStepName_NotEvaluatedAsRegisteredType()
    {
        // A PipelineJoin.Source can name either a registered type or a prior step (CTE). If the
        // RPC naively evaluated authorization for every join Source without filtering to
        // registry-resolvable ones first, a step-to-step join (here "enriched" joins its own
        // prior aggregate step "by_author") would incorrectly throw FailedPrecondition trying to
        // look up "by_author" as a registered schema — breaking every multi-step pipeline that
        // joins across its own steps.
        await _registry.RegisterAsync(SchemaFixtures.ArticleWithProjectionSchema());

        var agg = new PipelineStep { Name = "by_author" };
        agg.GroupBy.Add(new GroupKey { Field = "Category" });
        agg.Metrics.Add(new MetricSpec { Name = "n", Type = AggregationType.Count });

        var enriched = new PipelineStep { Name = "enriched", Reads = "base" };
        var join = new PipelineJoin { Source = "by_author", Kind = JoinKind.Inner };
        join.On.Add(new JoinCondition { Left = "Category", Right = "Category" });
        enriched.Joins.Add(join);
        enriched.Select.Add(new SelectItem { All = true });

        var request = new PipelineRequest { TypeName = "Article" };
        request.Steps.Add(agg);
        request.Steps.Add(enriched);

        var (writer, _) = MakeStream<SearchResponse>();
        var act = async () => await _sut.Pipeline(request, writer, TestServerCallContext.Create());

        await act.Should().NotThrowAsync();
        _search.ReceivedCalls().Should().NotBeEmpty(); // reached the search service — not denied, not blocked
    }

    [Fact]
    public async Task Pipeline_OwnerRestrictedCaller_ForwardsOwnerColumnAndCallerIdAsOwnerValue()
    {
        // This is the input that makes Build's primary-ownership wrap-and-AND (covered in
        // StarRocksPipelineBuilderTests) actually filter rows for this caller: proves the RPC
        // computes and forwards it correctly.
        await _registry.RegisterAsync(OwnedSchema("Owned", "OwnerId"));
        _actingUserAccessor.ActingUser = ActingUserFixtures.Principal("alice", "member");

        IReadOnlyDictionary<string, AuthorizationConstraint>? captured = null;
        _search.PipelineAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<PipelineRequest>(), Arg.Any<Func<string, StarRocksQuerySchema?>>(),
                Arg.Do<IReadOnlyDictionary<string, AuthorizationConstraint>?>(a => captured = a))
            .Returns(Enumerable.Empty<dynamic>());

        var request = new PipelineRequest { TypeName = "Owned" };
        var (writer, _) = MakeStream<SearchResponse>();
        await _sut.Pipeline(request, writer, TestServerCallContext.Create());

        captured.Should().NotBeNull();
        captured!["Owned"].OwnerColumn.Should().Be("OwnerId");
        captured["Owned"].OwnerValue.Should().Be("alice");
    }

    [Fact]
    public async Task Pipeline_BypassCaller_ForwardsUnrestrictedConstraint_NoOwnerFilter()
    {
        await _registry.RegisterAsync(OwnedSchema("Owned", "OwnerId"));

        IReadOnlyDictionary<string, AuthorizationConstraint>? captured = null;
        _search.PipelineAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<PipelineRequest>(), Arg.Any<Func<string, StarRocksQuerySchema?>>(),
                Arg.Do<IReadOnlyDictionary<string, AuthorizationConstraint>?>(a => captured = a))
            .Returns(Enumerable.Empty<dynamic>());

        var request = new PipelineRequest { TypeName = "Owned" };
        var (writer, _) = MakeStream<SearchResponse>();
        await _sut.Pipeline(request, writer, TestServerCallContext.Create());

        captured.Should().NotBeNull();
        captured!["Owned"].OwnerColumn.Should().BeNull();
        captured["Owned"].AllowedFields.Should().BeNull();
    }

    [Fact]
    public async Task Pipeline_JoinedTypeOwnerRestricted_ForwardsOwnerConstraintForJoinedType()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema()); // bypass role "test-bypass"
        await _registry.RegisterAsync(OwnedSchema("Article", "OwnerId", bypassRole: "other-bypass"));

        IReadOnlyDictionary<string, AuthorizationConstraint>? captured = null;
        _search.PipelineAsync(
                Arg.Any<StarRocksQuerySchema>(), Arg.Any<PipelineRequest>(), Arg.Any<Func<string, StarRocksQuerySchema?>>(),
                Arg.Do<IReadOnlyDictionary<string, AuthorizationConstraint>?>(a => captured = a))
            .Returns(Enumerable.Empty<dynamic>());

        var step = new PipelineStep { Name = "j" };
        var join = new PipelineJoin { Source = "Article", Kind = JoinKind.Left };
        join.On.Add(new JoinCondition { Left = "Id", Right = "AuthorId" });
        step.Joins.Add(join);
        step.Select.Add(new SelectItem { All = true });

        var request = new PipelineRequest { TypeName = "Author" };
        request.Steps.Add(step);

        var (writer, _) = MakeStream<SearchResponse>();
        await _sut.Pipeline(request, writer, TestServerCallContext.Create());

        captured.Should().NotBeNull();
        captured!["Article"].OwnerColumn.Should().Be("OwnerId");
        captured["Article"].OwnerValue.Should().Be("test-user"); // default fixture's sub claim
    }
}
