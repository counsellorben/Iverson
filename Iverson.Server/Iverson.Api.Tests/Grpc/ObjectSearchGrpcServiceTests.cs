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
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns(Enumerable.Empty<dynamic>());
        _search.AggregateAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns((SrAggResult?)null);
        _search.GroupByAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<GroupByRequest>(), Arg.Any<Func<string, EngagementQuerySchema?>>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns(Enumerable.Empty<dynamic>());
        _search.PipelineAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<PipelineRequest>(), Arg.Any<Func<string, EngagementQuerySchema?>>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns(Enumerable.Empty<dynamic>());

        _actingUserAccessor = new ActingUserAccessor
            { ActingUser = ActingUserFixtures.Principal("test-user", "test-bypass") };
        _sut = new ObjectSearchGrpcService(
            _registry, _search, _vector, _embedding,
            NullLogger<ObjectSearchGrpcService>.Instance,
            _actingUserAccessor, _authEvaluator, new IntelligenceTenantScope("test-signing-key-0123456789abcdef"),
            new ResultReranker(), new ResultDiversifier());
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
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns(new[] { (dynamic)fakeRow }.AsEnumerable());

        var (writer, written) = MakeStream<SearchResponse>();
        await _sut.Search(new SearchRequest { TypeName = "Author" }, writer, TestServerCallContext.Create());

        written.Should().HaveCount(1);
        await _search.Received(1).SearchAsync(
            Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
            Arg.Any<Func<string, EngagementQuerySchema?>?>(),
            Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>());
    }

    [Fact]
    public async Task Search_PassesCorrectTableSchema_ToSearchService()
    {
        // SQL generation itself is now StarRocksQueryBuilder's concern (covered by
        // StarRocksQueryBuilderTests in Iverson.StarRocks.Tests). This test verifies
        // ObjectSearchGrpcService converts the registered SchemaDescriptor to a
        // EngagementQuerySchema targeting the right table before delegating.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        EngagementQuerySchema? capturedSchema = null;
        _search.SearchAsync(
                Arg.Do<EngagementQuerySchema>(s => capturedSchema = s), Arg.Any<SearchQuery?>(), Arg.Any<int>(),
                Arg.Any<int>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
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
                Arg.Any<EngagementQuerySchema>(), Arg.Do<SearchQuery?>(q => capturedQuery = q), Arg.Any<int>(),
                Arg.Any<int>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
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
        TenantColumn   = "TenantId",
        Authorization  = new Iverson.Api.Schema.AuthorizationRules(
            ownerField,
            new List<Iverson.Api.Schema.RowPermission> { new(bypassRole, true, true, true) },
            fieldPermissions?.ToList() ?? [])
    };

    private static SchemaDescriptor OwnedQdrantSchema(
        string typeName, string? ownerField, IReadOnlyList<Iverson.Api.Schema.FieldPermission>? fieldPermissions = null,
        string bypassRole = "test-bypass") => new()
    {
        TypeName       = typeName,
        TableName      = typeName.ToLowerInvariant() + "s",
        CollectionName = typeName.ToLowerInvariant() + "s",
        KeyColumn      = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns  = [new ColumnDescriptor("Name", "text", false), new ColumnDescriptor("Secret", "text", true)],
        FkColumns      = [],
        VectorFields   = [new VectorDescriptor("Name", 768, "nomic-embed-text")],
        ChunkFields    = [new ChunkDescriptor("Secret", 512, 64, "nomic-embed-text", 768)],
        Relations      = [],
        TenantColumn   = "TenantId",
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
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
                Arg.Do<IReadOnlyDictionary<string, AuthorizationConstraint>?>(a => captured = a))
            .Returns(Enumerable.Empty<dynamic>());

        var (writer, _) = MakeStream<SearchResponse>();
        await _sut.Search(new SearchRequest { TypeName = "Owned" }, writer, TestServerCallContext.Create());

        captured.Should().NotBeNull();
        captured!["Owned"].OwnerColumn.Should().BeNull();
        captured["Owned"].AllowedFields.Should().BeNull();
    }

    [Fact]
    public async Task Search_BypassCaller_ForwardsTenantConstraint_EvenThoughOwnerColumnIsNull()
    {
        // The tenant boundary is strictly additive (Tasks 2/3's design): it must be forwarded
        // even for a bypass-role (CanReadAll) caller whose ownership predicate is skipped. This
        // proves the tenant constraint is gated independently of the ownership block, at the RPC
        // layer that feeds StarRocksQueryBuilder/StarRocksPipelineBuilder.
        await _registry.RegisterAsync(OwnedSchema("Owned", "OwnerId")); // bypass role "test-bypass"

        IReadOnlyDictionary<string, AuthorizationConstraint>? captured = null;
        _search.SearchAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
                Arg.Do<IReadOnlyDictionary<string, AuthorizationConstraint>?>(a => captured = a))
            .Returns(Enumerable.Empty<dynamic>());

        var (writer, _) = MakeStream<SearchResponse>();
        await _sut.Search(new SearchRequest { TypeName = "Owned" }, writer, TestServerCallContext.Create());

        captured.Should().NotBeNull();
        captured!["Owned"].OwnerColumn.Should().BeNull(); // bypass still short-circuits ownership
        captured["Owned"].TenantColumn.Should().Be("TenantId");
        captured["Owned"].TenantValue.Should().Be("test-tenant"); // default fixture's tenant_id claim
    }

    [Fact]
    public async Task Search_BypassCaller_CrossTenant_StreamsEmptyResult()
    {
        // CanReadAll grants unrestricted row access WITHIN a tenant, but the tenant boundary is
        // additive and applies even to bypass-role callers — no operator bypass. This simulates,
        // at the RPC layer, what the real `WHERE `TenantId` = @__tenantVal` predicate (already
        // proven correct at the SQL-generation level in StarRocksQueryBuilderTests) does at
        // execution time: the fake search-service stands in for the real database and only
        // "returns" the row when the forwarded constraint's tenant value matches the row's own
        // tenant, so a caller from a different tenant sees nothing even though their role would
        // otherwise grant full read access.
        await _registry.RegisterAsync(OwnedSchema("Owned", "OwnerId")); // bypass role "test-bypass"
        _actingUserAccessor.ActingUser = ActingUserFixtures.PrincipalWithTenant("test-user", "tenant-b", "test-bypass");

        var rowOwnedByTenantA = new Dictionary<string, object> { ["Id"] = "1", ["Name"] = "Alice" };
        _search.SearchAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns(callInfo =>
            {
                var authz = callInfo.ArgAt<IReadOnlyDictionary<string, AuthorizationConstraint>?>(7);
                var matches = authz is not null && authz["Owned"].TenantValue == "tenant-a";
                return matches
                    ? new[] { (dynamic)rowOwnedByTenantA }.AsEnumerable()
                    : Enumerable.Empty<dynamic>();
            });

        var (writer, written) = MakeStream<SearchResponse>();
        await _sut.Search(new SearchRequest { TypeName = "Owned" }, writer, TestServerCallContext.Create());

        written.Should().BeEmpty();
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
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
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
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
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
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
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
    public async Task Search_JoinedTypeTenantConstraint_ForwardsTenantColumnAndCallerTenantAsValue()
    {
        // Same reasoning as the joined-type ownership test above, but for the tenant boundary:
        // the joined type's constraint must carry the caller's own tenant_id claim so
        // BuildFromWithJoins can append the tenant condition to that JOIN's ON clause (with its
        // own per-join unique parameter name to avoid colliding with any other joined type).
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema()); // bypass role "test-bypass"
        await _registry.RegisterAsync(OwnedSchema("Article", "OwnerId", bypassRole: "other-bypass"));

        IReadOnlyDictionary<string, AuthorizationConstraint>? captured = null;
        _search.SearchAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
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
        captured!["Article"].TenantColumn.Should().Be("TenantId");
        captured["Article"].TenantValue.Should().Be("test-tenant"); // default fixture's tenant_id claim
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
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
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
    public async Task Search_RestrictedSortFieldRejectedByBuilder_TranslatesToInvalidArgument()
    {
        // Simulates what BuildOrder (EngagementQueryTranslationException on a disallowed
        // query.Sort entry — see StarRocksQueryBuilderTests) causes the real search service to
        // throw; proves ObjectSearchGrpcService.Search still surfaces it as InvalidArgument.
        // Closes a whole-branch-review gap: sort was previously ungated even though Search's
        // filter clauses already were.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        _search.SearchAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns<Task<IEnumerable<dynamic>>>(_ => throw new EngagementQueryTranslationException(
                "Sort property 'Bio' on 'Author' is not authorized for this caller."));

        var request = new SearchRequest { TypeName = "Author", Query = new SearchQuery() };
        request.Query.Sort.Add(new SearchSort { Property = "Bio" });

        var (writer, _) = MakeStream<SearchResponse>();
        var act = async () => await _sut.Search(request, writer, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument && e.Status.Detail.Contains("Bio"));
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
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
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
    public async Task Aggregate_TranslatesEngagementQueryTranslationException_ToInvalidArgument()
    {
        // The multi-key-GROUP-BY guard now lives in EngagementRepository.AggregateAsync
        // (covered by EngagementRepositorySearchTests). This test verifies
        // ObjectSearchGrpcService still correctly translates a
        // EngagementQueryTranslationException raised by the search service into an
        // InvalidArgument RpcException.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        _search.AggregateAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns<Task<SrAggResult?>>(_ => throw new EngagementQueryTranslationException(
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
        // EngagementRepository.AggregateAsync (covered by EngagementRepositorySearchTests
        // and Task 5's integration tests). This test mocks the search service to
        // return the already-decoded AggregationResult directly.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        _search.AggregateAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
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
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
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
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
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
                Arg.Any<EngagementQuerySchema>(), Arg.Do<SearchQuery?>(q => capturedQuery = q),
                Arg.Any<AggregationDescriptor>(), Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
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
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
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
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
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
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
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
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
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
        // Simulates what BuildAggregate (EngagementQueryTranslationException on a disallowed
        // spec.Field — see StarRocksQueryBuilderTests) causes the real search service to throw;
        // proves ObjectSearchGrpcService.Aggregate still surfaces it as InvalidArgument even
        // though authorization is now evaluated before dispatch.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        _search.AggregateAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns<Task<SrAggResult?>>(_ => throw new EngagementQueryTranslationException(
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
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns<Task<SrAggResult?>>(_ => throw new EngagementQueryTranslationException(
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
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Do<IReadOnlyList<string>?>(f => capturedFields = f), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
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

    // ── SearchSimilar — authorization ──────────────────────────────────────────

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
            Relations      = [],
            TenantColumn   = "TenantId",
            Authorization  = new Iverson.Api.Schema.AuthorizationRules(
                null,
                new List<Iverson.Api.Schema.RowPermission> { new("test-bypass", true, true, true) },
                new List<Iverson.Api.Schema.FieldPermission>())
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

        _vector.SearchNamedAsync("articles_test-tenant", "title_vector", fakeVector, Arg.Any<ulong>(), Arg.Any<Filter>())
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
        _vector.SearchNamedAsync("articles_test-tenant", "title_vector", fakeVector, Arg.Any<ulong>(), Arg.Any<Filter>())
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

    [Fact]
    public async Task SearchSimilar_FilterOnTheServerOwnedTenantColumn_ThrowsInvalidArgument()
    {
        // Task 1: __TenantId is a real ScalarColumns member, but it is not addressable by clients.
        // A filter naming it must be rejected exactly like any unknown property — otherwise a
        // caller could probe or override the tenant boundary through the search filter.
        var schema = SchemaFixtures.ArticleSchema() with
        {
            ScalarColumns =
            [
                new ColumnDescriptor("Title", "text", false),
                new ColumnDescriptor(SchemaDescriptor.TenantColumnName, "TEXT", false)
            ],
            TenantColumn = SchemaDescriptor.TenantColumnName
        };
        await _registry.RegisterAsync(schema);
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(new float[768]);

        var request = new SearchSimilarRequest { TypeName = "Article", Property = "Title", Query = "q", TopK = 5 };
        request.Filter.Add(new SearchClause
        {
            Property = SchemaDescriptor.TenantColumnName, Operator = SearchOperator.Equals,
            Value = new SearchValue { StringVal = "other-tenant" }, ClauseType = SearchClauseType.Filter
        });

        var (writer, _) = MakeStream<SearchResponse>();
        var act = async () => await _sut.SearchSimilar(request, writer, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument
                     && e.Status.Detail.Contains("is not a scalar or foreign-key column"));
    }

    [Fact]
    public async Task SearchSimilar_NoAuthorizationRules_ReturnsEmptyStream_WithoutQueryingQdrant()
    {
        var schema = SchemaFixtures.ArticleSchema() with { Authorization = null };
        await _registry.RegisterAsync(schema);

        var (writer, written) = MakeStream<SearchResponse>();
        await _sut.SearchSimilar(
            new SearchSimilarRequest { TypeName = "Article", Property = "Title", Query = "q" },
            writer, TestServerCallContext.Create());

        written.Should().BeEmpty();
        await _vector.DidNotReceive().SearchNamedAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<ulong>(), Arg.Any<Filter>());
    }

    [Fact]
    public async Task SearchSimilar_NoActingUserIdentity_ReturnsEmptyStream()
    {
        await _registry.RegisterAsync(OwnedQdrantSchema("Owned", "OwnerId"));
        _actingUserAccessor.ActingUser = null;

        var (writer, written) = MakeStream<SearchResponse>();
        await _sut.SearchSimilar(
            new SearchSimilarRequest { TypeName = "Owned", Property = "Name", Query = "q" },
            writer, TestServerCallContext.Create());

        written.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchSimilar_OwnershipRequired_AddsMatchKeywordConditionToFilter()
    {
        // No caller-supplied filter clause — also proves a fresh Filter is constructed when none exists.
        // The tenant boundary is enforced by collection routing (Task 3), not by a query-time filter
        // condition, so only the ownership condition is expected here.
        await _registry.RegisterAsync(OwnedQdrantSchema("Owned", "OwnerId", bypassRole: "other-bypass"));
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(new float[768]);
        _vector.SearchNamedAsync("owneds_test-tenant", "name_vector", Arg.Any<float[]>(), Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(new List<VectorSearchResult>().AsReadOnly());

        var (writer, _) = MakeStream<SearchResponse>();
        await _sut.SearchSimilar(
            new SearchSimilarRequest { TypeName = "Owned", Property = "Name", Query = "q" },
            writer, TestServerCallContext.Create());

        var call = _vector.ReceivedCalls()
            .Should().ContainSingle(c => c.GetMethodInfo().Name == nameof(IVectorQueryService.SearchNamedAsync))
            .Subject;
        var captured = (Filter?)call.GetArguments()[4];
        captured.Should().NotBeNull();
        captured!.Must.Should().ContainSingle(c => c.Field.Key == "ownerId" && c.Field.Match.Keyword == "test-user");
    }

    [Fact]
    public async Task SearchSimilar_BypassRole_NoOwnershipFilterAdded()
    {
        // Bypass role short-circuits ownership filtering, and the tenant boundary is enforced by
        // collection routing (Task 3) rather than a query-time filter condition — with no
        // caller-supplied filter clause either, no Filter is built at all.
        await _registry.RegisterAsync(OwnedQdrantSchema("Owned", "OwnerId")); // bypassRole defaults to "test-bypass"
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(new float[768]);
        _vector.SearchNamedAsync("owneds_test-tenant", "name_vector", Arg.Any<float[]>(), Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(new List<VectorSearchResult>().AsReadOnly());

        var (writer, _) = MakeStream<SearchResponse>();
        await _sut.SearchSimilar(
            new SearchSimilarRequest { TypeName = "Owned", Property = "Name", Query = "q" },
            writer, TestServerCallContext.Create());

        var call = _vector.ReceivedCalls()
            .Should().ContainSingle(c => c.GetMethodInfo().Name == nameof(IVectorQueryService.SearchNamedAsync))
            .Subject;
        var captured = (Filter?)call.GetArguments()[4];
        captured.Should().BeNull();
    }

    [Fact]
    public async Task SearchSimilar_RestrictedSearchedProperty_ThrowsInvalidArgument()
    {
        var fieldPermissions = new List<Iverson.Api.Schema.FieldPermission> { new("Name", ["admin"], []) };
        await _registry.RegisterAsync(OwnedQdrantSchema("Owned", null, fieldPermissions));

        var (writer, _) = MakeStream<SearchResponse>();
        var act = async () => await _sut.SearchSimilar(
            new SearchSimilarRequest { TypeName = "Owned", Property = "Name", Query = "q" },
            writer, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task SearchSimilar_RestrictedFilterClauseProperty_ThrowsInvalidArgument()
    {
        var fieldPermissions = new List<Iverson.Api.Schema.FieldPermission> { new("Secret", ["admin"], []) };
        await _registry.RegisterAsync(OwnedQdrantSchema("Owned", null, fieldPermissions));
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(new float[768]);

        var request = new SearchSimilarRequest { TypeName = "Owned", Property = "Name", Query = "q" };
        request.Filter.Add(new SearchClause
        {
            Property = "Secret", Operator = SearchOperator.Equals,
            Value = new SearchValue { StringVal = "x" }, ClauseType = SearchClauseType.Filter
        });

        var (writer, _) = MakeStream<SearchResponse>();
        var act = async () => await _sut.SearchSimilar(request, writer, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task SearchSimilar_RestrictedField_MaskedFromResponse_ButKeyEntrySurvives()
    {
        var fieldPermissions = new List<Iverson.Api.Schema.FieldPermission> { new("Secret", ["admin"], []) };
        await _registry.RegisterAsync(OwnedQdrantSchema("Owned", null, fieldPermissions));
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(new float[768]);

        var vectorResult = new VectorSearchResult(
            Id: 1, Score: 0.9,
            Payload: new Dictionary<string, string> { ["key"] = "point-key-1", ["name"] = "visible", ["secret"] = "hidden" });
        _vector.SearchNamedAsync("owneds_test-tenant", "name_vector", Arg.Any<float[]>(), Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(new List<VectorSearchResult> { vectorResult }.AsReadOnly());

        var (writer, written) = MakeStream<SearchResponse>();
        await _sut.SearchSimilar(
            new SearchSimilarRequest { TypeName = "Owned", Property = "Name", Query = "q" },
            writer, TestServerCallContext.Create());

        written.Should().HaveCount(1);
        written[0].Data.Fields.Should().ContainKey("Id");   // identity field survives because "Id" is in AllowedFields — the key column is seeded into allFields at RowFieldAuthorizationEvaluator.cs:84 and filtered out of the exclusion set at :76, so it can never be excluded
        written[0].Data.Fields.Should().ContainKey("Name");
        written[0].Data.Fields.Should().NotContainKey("Secret");
    }

    [Fact]
    public async Task SearchSimilar_QdrantThrowsNotFound_ReturnsEmptyStream()
    {
        // Design spec: a brand-new tenant whose collection was never created via
        // EnsureCollectionAsync gets a Qdrant NotFound on its very first search — this must be
        // treated as an empty result set, not surfaced as a raw RpcException to the caller.
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var fakeVector = new float[768];
        _embedding.EmbedAsync("test query", Arg.Any<CancellationToken>()).Returns(fakeVector);
        _vector.SearchNamedAsync("articles_test-tenant", "title_vector", fakeVector, Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns<Task<IReadOnlyList<VectorSearchResult>>>(_ => throw new RpcException(new Status(StatusCode.NotFound, "collection not found")));

        var (writer, written) = MakeStream<SearchResponse>();
        await _sut.SearchSimilar(
            new SearchSimilarRequest { TypeName = "Article", Property = "Title", Query = "test query", TopK = 5 },
            writer, TestServerCallContext.Create());

        written.Should().BeEmpty();
    }

    // ── SearchChunks — authorization ───────────────────────────────────────────

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

        _vector.SearchNamedAsync("articles_chunks_test-tenant", "body_vector", fakeVector, Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(new List<VectorSearchResult> { chunkResult }.AsReadOnly());

        var (writer, written) = MakeStream<ChunkSearchResponse>();
        await _sut.SearchChunks(
            new SearchChunksRequest { TypeName = "Article", Property = "Body", Query = "test query", TopK = 5 },
            writer, TestServerCallContext.Create());

        await _vector.Received(1).SearchNamedAsync(
            "articles_chunks_test-tenant", Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<ulong>(), Arg.Any<Filter>());
        written.Should().HaveCount(1);
    }

    [Fact]
    public async Task SearchChunks_QdrantThrowsNotFound_ReturnsEmptyStream()
    {
        // Design spec: a brand-new tenant whose chunks collection was never created via
        // EnsureCollectionAsync gets a Qdrant NotFound on its very first search — this must be
        // treated as an empty result set, not surfaced as a raw RpcException to the caller.
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var fakeVector = new float[768];
        _embedding.EmbedAsync("test query", Arg.Any<CancellationToken>()).Returns(fakeVector);
        _vector.SearchNamedAsync("articles_chunks_test-tenant", "body_vector", fakeVector, Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns<Task<IReadOnlyList<VectorSearchResult>>>(_ => throw new RpcException(new Status(StatusCode.NotFound, "collection not found")));

        var (writer, written) = MakeStream<ChunkSearchResponse>();
        await _sut.SearchChunks(
            new SearchChunksRequest { TypeName = "Article", Property = "Body", Query = "test query", TopK = 5 },
            writer, TestServerCallContext.Create());

        written.Should().BeEmpty();
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

        _vector.SearchNamedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<ulong>(), Arg.Any<Filter>())
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
        _vector.SearchNamedAsync("articles_chunks_test-tenant", "body_vector", Arg.Any<float[]>(), Arg.Any<ulong>(), Arg.Any<Filter>())
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
        // Only the user-supplied parent_id filter — the tenant boundary is enforced by collection
        // routing (Task 3), not a query-time filter condition.
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
    public async Task SearchChunks_WithMetadataColumnEqualsFilter_PassesCamelCasePayloadMatch()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema() with { MetadataColumns = ["Title"] });
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(new float[768]);
        _vector.SearchNamedAsync("articles_chunks_test-tenant", "body_vector", Arg.Any<float[]>(), Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(new List<VectorSearchResult>().AsReadOnly());

        var request = new SearchChunksRequest { TypeName = "Article", Property = "Body", Query = "q", TopK = 5 };
        request.Filter.Add(new SearchClause
        {
            Property = "Title", Operator = SearchOperator.Equals,
            Value = new SearchValue { StringVal = "news" }, ClauseType = SearchClauseType.Filter
        });

        var (writer, _) = MakeStream<ChunkSearchResponse>();
        await _sut.SearchChunks(request, writer, TestServerCallContext.Create());

        var call = _vector.ReceivedCalls()
            .Should().ContainSingle(c => c.GetMethodInfo().Name == nameof(IVectorQueryService.SearchNamedAsync))
            .Subject;
        var captured = (Filter?)call.GetArguments()[4];
        captured.Should().NotBeNull();
        captured!.Must.Should().ContainSingle(c =>
            c.Field.Key == "title" && c.Field.Match.Keyword == "news");
    }

    [Fact]
    public async Task SearchChunks_MultipleClausesMixingPkAndMetadata_AreAllTranslated()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema() with { MetadataColumns = ["Title"] });
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(new float[768]);
        _vector.SearchNamedAsync("articles_chunks_test-tenant", "body_vector", Arg.Any<float[]>(), Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(new List<VectorSearchResult>().AsReadOnly());

        var request = new SearchChunksRequest { TypeName = "Article", Property = "Body", Query = "q", TopK = 5 };
        request.Filter.Add(new SearchClause
        {
            Property = "Id", Operator = SearchOperator.Equals,
            Value = new SearchValue { StringVal = "parent-123" }, ClauseType = SearchClauseType.Filter
        });
        request.Filter.Add(new SearchClause
        {
            Property = "Title", Operator = SearchOperator.Equals,
            Value = new SearchValue { StringVal = "news" }, ClauseType = SearchClauseType.Filter
        });

        var (writer, _) = MakeStream<ChunkSearchResponse>();
        await _sut.SearchChunks(request, writer, TestServerCallContext.Create());

        var call = _vector.ReceivedCalls()
            .Should().ContainSingle(c => c.GetMethodInfo().Name == nameof(IVectorQueryService.SearchNamedAsync))
            .Subject;
        var captured = (Filter?)call.GetArguments()[4];
        captured.Should().NotBeNull();
        captured!.Must.Should().Contain(c => c.Field.Key == "parent_id" && c.Field.Match.Keyword == "parent-123");
        captured.Must.Should().Contain(c => c.Field.Key == "title" && c.Field.Match.Keyword == "news");
    }

    [Fact]
    public async Task SearchChunks_MetadataFilterWithDifferentCasing_UsesSchemaCanonicalPayloadKey()
    {
        // Schema declares "Title"; caller filters as "TITLE". The payload key must still be
        // "title" (what IntelligenceStoreConsumer wrote), not "tITLE".
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema() with { MetadataColumns = ["Title"] });
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(new float[768]);
        _vector.SearchNamedAsync("articles_chunks_test-tenant", "body_vector", Arg.Any<float[]>(), Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(new List<VectorSearchResult>().AsReadOnly());

        var request = new SearchChunksRequest { TypeName = "Article", Property = "Body", Query = "q", TopK = 5 };
        request.Filter.Add(new SearchClause
        {
            Property = "TITLE", Operator = SearchOperator.Equals,
            Value = new SearchValue { StringVal = "news" }, ClauseType = SearchClauseType.Filter
        });

        var (writer, _) = MakeStream<ChunkSearchResponse>();
        await _sut.SearchChunks(request, writer, TestServerCallContext.Create());

        var call = _vector.ReceivedCalls()
            .Should().ContainSingle(c => c.GetMethodInfo().Name == nameof(IVectorQueryService.SearchNamedAsync))
            .Subject;
        var captured = (Filter?)call.GetArguments()[4];
        captured.Should().NotBeNull();
        captured!.Must.Should().ContainSingle(c =>
            c.Field.Key == "title" && c.Field.Match.Keyword == "news");
    }

    [Fact]
    public async Task SearchChunks_MetadataFilterWithUnsupportedValueKind_ThrowsInvalidArgument()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema() with { MetadataColumns = ["Title"] });
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(new float[768]);

        var request = new SearchChunksRequest { TypeName = "Article", Property = "Body", Query = "q", TopK = 5 };
        request.Filter.Add(new SearchClause
        {
            Property = "Title", Operator = SearchOperator.Equals,
            Value = new SearchValue { StringList = new RepeatedString { Values = { "a", "b" } } },
            ClauseType = SearchClauseType.Filter
        });

        var (writer, _) = MakeStream<ChunkSearchResponse>();
        var act = async () => await _sut.SearchChunks(request, writer, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task SearchChunks_MetadataFilterOnUnauthorizedField_ThrowsInvalidArgument()
    {
        // "Name" is a metadata column but restricted to "admin"; the acting user has only
        // "test-bypass", so filtering on it would be a value oracle for a field it cannot read.
        var fieldPermissions = new List<Iverson.Api.Schema.FieldPermission> { new("Name", ["admin"], []) };
        var schema = OwnedQdrantSchema("Owned", null, fieldPermissions) with { MetadataColumns = ["Name"] };
        await _registry.RegisterAsync(schema);
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(new float[768]);

        var request = new SearchChunksRequest { TypeName = "Owned", Property = "Secret", Query = "q" };
        request.Filter.Add(new SearchClause
        {
            Property = "Name", Operator = SearchOperator.Equals,
            Value = new SearchValue { StringVal = "x" }, ClauseType = SearchClauseType.Filter
        });

        var (writer, _) = MakeStream<ChunkSearchResponse>();
        var act = async () => await _sut.SearchChunks(request, writer, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument
                     && e.Status.Detail.Contains("not authorized for this caller"));
    }

    [Fact]
    public async Task SearchChunks_MetadataFilterOnAuthorizedField_IsAccepted()
    {
        var fieldPermissions = new List<Iverson.Api.Schema.FieldPermission> { new("Name", ["test-bypass"], []) };
        var schema = OwnedQdrantSchema("Owned", null, fieldPermissions) with { MetadataColumns = ["Name"] };
        await _registry.RegisterAsync(schema);
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(new float[768]);
        _vector.SearchNamedAsync("owneds_chunks_test-tenant", "secret_vector", Arg.Any<float[]>(), Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(new List<VectorSearchResult>().AsReadOnly());

        var request = new SearchChunksRequest { TypeName = "Owned", Property = "Secret", Query = "q" };
        request.Filter.Add(new SearchClause
        {
            Property = "Name", Operator = SearchOperator.Equals,
            Value = new SearchValue { StringVal = "x" }, ClauseType = SearchClauseType.Filter
        });

        var (writer, _) = MakeStream<ChunkSearchResponse>();
        await _sut.SearchChunks(request, writer, TestServerCallContext.Create());

        var call = _vector.ReceivedCalls()
            .Should().ContainSingle(c => c.GetMethodInfo().Name == nameof(IVectorQueryService.SearchNamedAsync))
            .Subject;
        var captured = (Filter?)call.GetArguments()[4];
        captured.Should().NotBeNull();
        captured!.Must.Should().ContainSingle(c => c.Field.Key == "name" && c.Field.Match.Keyword == "x");
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

    [Fact]
    public async Task SearchChunks_NoAuthorizationRules_ReturnsEmptyStream_WithoutQueryingQdrant()
    {
        var schema = SchemaFixtures.ArticleSchema() with { Authorization = null };
        await _registry.RegisterAsync(schema);

        var (writer, written) = MakeStream<ChunkSearchResponse>();
        await _sut.SearchChunks(
            new SearchChunksRequest { TypeName = "Article", Property = "Body", Query = "q" },
            writer, TestServerCallContext.Create());

        written.Should().BeEmpty();
        await _vector.DidNotReceive().SearchNamedAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<ulong>(), Arg.Any<Filter>());
    }

    [Fact]
    public async Task SearchChunks_NoActingUserIdentity_ReturnsEmptyStream()
    {
        await _registry.RegisterAsync(OwnedQdrantSchema("Owned", "OwnerId"));
        _actingUserAccessor.ActingUser = null;

        var (writer, written) = MakeStream<ChunkSearchResponse>();
        await _sut.SearchChunks(
            new SearchChunksRequest { TypeName = "Owned", Property = "Secret", Query = "q" },
            writer, TestServerCallContext.Create());

        written.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchChunks_RestrictedSearchedProperty_ThrowsInvalidArgument()
    {
        var fieldPermissions = new List<Iverson.Api.Schema.FieldPermission> { new("Secret", ["admin"], []) };
        await _registry.RegisterAsync(OwnedQdrantSchema("Owned", null, fieldPermissions));

        var (writer, _) = MakeStream<ChunkSearchResponse>();
        var act = async () => await _sut.SearchChunks(
            new SearchChunksRequest { TypeName = "Owned", Property = "Secret", Query = "q" },
            writer, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task SearchChunks_KeyColumnFilterClause_NeverRejected_RegardlessOfAllowedFields()
    {
        // "Name" is restricted, but the request neither searches nor filters on it — proves
        // BuildChunksFilter's single EQUALS-on-key-column clause needs no AllowedFields check.
        var fieldPermissions = new List<Iverson.Api.Schema.FieldPermission> { new("Name", ["admin"], []) };
        await _registry.RegisterAsync(OwnedQdrantSchema("Owned", null, fieldPermissions));
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(new float[768]);
        _vector.SearchNamedAsync("owneds_chunks_test-tenant", "secret_vector", Arg.Any<float[]>(), Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(new List<VectorSearchResult>().AsReadOnly());

        var request = new SearchChunksRequest { TypeName = "Owned", Property = "Secret", Query = "q" };
        request.Filter.Add(new SearchClause
        {
            Property = "Id", Operator = SearchOperator.Equals,
            Value = new SearchValue { StringVal = "parent-1" }, ClauseType = SearchClauseType.Filter
        });

        var (writer, _) = MakeStream<ChunkSearchResponse>();
        var act = async () => await _sut.SearchChunks(request, writer, TestServerCallContext.Create());

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SearchChunks_OwnershipRequired_MergesMatchKeywordConditionWithKeyFilter()
    {
        await _registry.RegisterAsync(OwnedQdrantSchema("Owned", "OwnerId", bypassRole: "other-bypass"));
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(new float[768]);
        _vector.SearchNamedAsync("owneds_chunks_test-tenant", "secret_vector", Arg.Any<float[]>(), Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(new List<VectorSearchResult>().AsReadOnly());

        var request = new SearchChunksRequest { TypeName = "Owned", Property = "Secret", Query = "q" };
        request.Filter.Add(new SearchClause
        {
            Property = "Id", Operator = SearchOperator.Equals,
            Value = new SearchValue { StringVal = "parent-1" }, ClauseType = SearchClauseType.Filter
        });

        var (writer, _) = MakeStream<ChunkSearchResponse>();
        await _sut.SearchChunks(request, writer, TestServerCallContext.Create());

        var call = _vector.ReceivedCalls()
            .Should().ContainSingle(c => c.GetMethodInfo().Name == nameof(IVectorQueryService.SearchNamedAsync))
            .Subject;
        var captured = (Filter?)call.GetArguments()[4];
        captured.Should().NotBeNull();
        captured!.Must.Should().Contain(c => c.Field.Key == "ownerId" && c.Field.Match.Keyword == "test-user");
        captured.Must.Should().Contain(c => c.Field.Key == "parent_id" && c.Field.Match.Keyword == "parent-1");
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
                Arg.Any<EngagementQuerySchema>(), Arg.Any<GroupByRequest>(), Arg.Any<Func<string, EngagementQuerySchema?>>(),
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
                Arg.Any<EngagementQuerySchema>(), Arg.Any<GroupByRequest>(), Arg.Any<Func<string, EngagementQuerySchema?>>(),
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
                Arg.Any<EngagementQuerySchema>(), Arg.Any<GroupByRequest>(), Arg.Any<Func<string, EngagementQuerySchema?>>(),
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
        // Simulates what BuildGroupBy (EngagementQueryTranslationException on a disallowed
        // request.Keys entry — see StarRocksQueryBuilderTests) causes the real search service
        // to throw; proves ObjectSearchGrpcService.GroupBy still surfaces it as InvalidArgument
        // even though authorization is now evaluated before dispatch.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        _search.GroupByAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<GroupByRequest>(), Arg.Any<Func<string, EngagementQuerySchema?>>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns<Task<IEnumerable<dynamic>>>(_ => throw new EngagementQueryTranslationException(
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
                Arg.Any<EngagementQuerySchema>(), Arg.Any<GroupByRequest>(), Arg.Any<Func<string, EngagementQuerySchema?>>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns<Task<IEnumerable<dynamic>>>(_ => throw new EngagementQueryTranslationException(
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
                Arg.Any<EngagementQuerySchema>(), Arg.Any<GroupByRequest>(), Arg.Any<Func<string, EngagementQuerySchema?>>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns<Task<IEnumerable<dynamic>>>(_ => throw new EngagementQueryTranslationException(
                "Field 'Bio' on 'Author' referenced by metric 'revenue' expression is not authorized for this caller."));

        var request = new GroupByRequest { TypeName = "Author", Keys = { "Name" } };
        request.Metrics.Add(new MetricSpec { Name = "revenue", Type = AggregationType.Sum, Expression = "Bio * 2" });

        var (writer, _) = MakeStream<SearchResponse>();
        var act = async () => await _sut.GroupBy(request, writer, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument && e.Status.Detail.Contains("Bio"));
    }

    [Fact]
    public async Task GroupBy_RestrictedOrderByFieldRejectedByBuilder_TranslatesToInvalidArgument()
    {
        // Simulates what BuildGroupBy's orderSql gate (EngagementQueryTranslationException on a
        // disallowed request.OrderBy entry — see StarRocksQueryBuilderTests) causes the real
        // search service to throw; proves ObjectSearchGrpcService.GroupBy still surfaces it as
        // InvalidArgument. Closes a whole-branch-review gap: OrderBy was previously ungated even
        // though Keys/Metrics on this same request already were.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        _search.GroupByAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<GroupByRequest>(), Arg.Any<Func<string, EngagementQuerySchema?>>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns<Task<IEnumerable<dynamic>>>(_ => throw new EngagementQueryTranslationException(
                "ORDER BY property 'Bio' on 'Author' is not authorized for this caller."));

        var request = new GroupByRequest { TypeName = "Author", Keys = { "Name" } };
        request.Metrics.Add(new MetricSpec { Name = "cnt", Type = AggregationType.Count });
        request.OrderBy.Add(new SearchSort { Property = "Bio" });

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
                Arg.Any<EngagementQuerySchema>(), Arg.Do<PipelineRequest>(r => capturedRequest = r),
                Arg.Any<Func<string, EngagementQuerySchema?>>(),
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
    public async Task Pipeline_TranslatesEngagementQueryTranslationException_ToInvalidArgument()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        _search.PipelineAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<PipelineRequest>(), Arg.Any<Func<string, EngagementQuerySchema?>>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns<Task<IEnumerable<dynamic>>>(_ => throw new EngagementQueryTranslationException(
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
                Arg.Any<EngagementQuerySchema>(), Arg.Any<PipelineRequest>(), Arg.Any<Func<string, EngagementQuerySchema?>>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns<Task<IEnumerable<dynamic>>>(_ => throw new EngagementNotReadyException("warming up"));

        var request = new PipelineRequest { TypeName = "Article" };
        var (writer, _) = MakeStream<SearchResponse>();

        var act = async () => await _sut.Pipeline(request, writer, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.Status.StatusCode == StatusCode.Unavailable);
    }

    [Fact]
    public async Task Pipeline_EngagementStoreDisabled_ThrowsFailedPrecondition()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleWithProjectionSchema());
        _search.PipelineAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<PipelineRequest>(), Arg.Any<Func<string, EngagementQuerySchema?>>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns<Task<IEnumerable<dynamic>>>(_ => throw new EngagementStoreDisabledException("engagement store disabled"));

        var request = new PipelineRequest { TypeName = "Article" };
        var (writer, _) = MakeStream<SearchResponse>();

        var act = async () => await _sut.Pipeline(request, writer, TestServerCallContext.Create());

        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.Status.StatusCode == StatusCode.FailedPrecondition);
    }

    [Fact]
    public async Task Pipeline_StreamsResults_PropagatesTraceId()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleWithProjectionSchema());
        _search.PipelineAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<PipelineRequest>(), Arg.Any<Func<string, EngagementQuerySchema?>>(),
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
    // StarRocksPipelineBuilder's (and EngagementRepository's) concern — covered end-to-end, with
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
                Arg.Any<EngagementQuerySchema>(), Arg.Any<PipelineRequest>(), Arg.Any<Func<string, EngagementQuerySchema?>>(),
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
                Arg.Any<EngagementQuerySchema>(), Arg.Any<PipelineRequest>(), Arg.Any<Func<string, EngagementQuerySchema?>>(),
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
                Arg.Any<EngagementQuerySchema>(), Arg.Any<PipelineRequest>(), Arg.Any<Func<string, EngagementQuerySchema?>>(),
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

    // ── Tensor re-ranking ──────────────────────────────────────────────────────

    // A property carrying BOTH [IversonEmbedding] and [IversonChunk] — the only shape for which
    // SearchSimilar has a "<property>_centroid" named vector to fetch on the object collection.
    private static SchemaDescriptor DualAnnotatedSchema() => new()
    {
        TypeName       = "Doc",
        TableName      = "docs",
        CollectionName = "docs",
        KeyColumn      = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns  = [new ColumnDescriptor("Body", "text", false)],
        FkColumns      = [],
        VectorFields   = [new VectorDescriptor("Body", 768, "nomic-embed-text")],
        ChunkFields    = [new ChunkDescriptor("Body", 512, 64, "nomic-embed-text", 768)],
        Relations      = [],
        Authorization  = new Iverson.Api.Schema.AuthorizationRules(
            null, new List<Iverson.Api.Schema.RowPermission> { new("test-bypass", true, true, true) }, []),
        TenantColumn   = "TenantId"
    };

    // Embedding-only searched property (no centroid possible) but a TIMESTAMPTZ metadata column,
    // so DecayFieldResolver DOES resolve a decay field — only one of the two signals is absent.
    private static SchemaDescriptor EmbeddingOnlyWithDecaySchema() => new()
    {
        TypeName        = "Dated",
        TableName       = "dated",
        CollectionName  = "dated",
        KeyColumn       = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns   = [
            new ColumnDescriptor("Title",       "text",        false),
            new ColumnDescriptor("PublishedAt", "TIMESTAMPTZ", true)
        ],
        MetadataColumns = ["PublishedAt"],
        FkColumns       = [],
        VectorFields    = [new VectorDescriptor("Title", 768, "nomic-embed-text")],
        ChunkFields     = [],
        Relations       = [],
        Authorization   = new Iverson.Api.Schema.AuthorizationRules(
            null, new List<Iverson.Api.Schema.RowPermission> { new("test-bypass", true, true, true) }, []),
        TenantColumn    = "TenantId"
    };

    private static float[] UnitVector()
    {
        var v = new float[768];
        v[0] = 1f;
        return v;
    }

    private static ulong CapturedLimit(IVectorQueryService vector) =>
        (ulong)vector.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IVectorQueryService.SearchNamedAsync))
            .GetArguments()[3]!;

    // Doc.Body is dual-annotated, so a centroid CAN be present — the identity gate must not fire
    // and the over-fetch stays exactly 4 × top_k with no ceiling.
    [Fact]
    public async Task SearchSimilar_OverFetchesFourTimesTopK_AndTrimsToTopK()
    {
        await _registry.RegisterAsync(DualAnnotatedSchema());

        var fakeVector = UnitVector();
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(fakeVector);

        var results = Enumerable.Range(1, 20)
            .Select(i => new VectorSearchResult((ulong)i, 1.0 - i * 0.01,
                new Dictionary<string, string> { ["body"] = $"a{i}" }))
            .ToList();
        _vector.SearchNamedAsync("docs_test-tenant", "body_vector", fakeVector, Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(results.AsReadOnly());
        _vector.RetrieveNamedVectorAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<ulong>>(), Arg.Any<string>())
               .Returns((IReadOnlyDictionary<ulong, float[]>)new Dictionary<ulong, float[]>());

        var (writer, written) = MakeStream<SearchResponse>();
        await _sut.SearchSimilar(
            new SearchSimilarRequest { TypeName = "Doc", Property = "Body", Query = "q", TopK = 5 },
            writer, TestServerCallContext.Create());

        CapturedLimit(_vector).Should().Be(20);   // 4 × top_k, uncapped
        written.Should().HaveCount(5);            // trimmed back to top_k
    }

    // Article.Title is embedding-only (no centroid possible) AND ArticleSchema has no timestamp
    // metadata column (no decay field) — the fused score provably equals the base score for every
    // candidate, so the over-fetch and the centroid round trip are both pure waste.
    [Fact]
    public async Task SearchSimilar_NoCentroidAndNoDecayField_RequestsExactlyTopK_AndSkipsRetrieve()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var fakeVector = UnitVector();
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(fakeVector);

        var results = Enumerable.Range(1, 5)
            .Select(i => new VectorSearchResult((ulong)i, 1.0 - i * 0.01,
                new Dictionary<string, string> { ["title"] = $"a{i}" }))
            .ToList();
        _vector.SearchNamedAsync("articles_test-tenant", "title_vector", fakeVector, Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(results.AsReadOnly());

        var (writer, written) = MakeStream<SearchResponse>();
        await _sut.SearchSimilar(
            new SearchSimilarRequest { TypeName = "Article", Property = "Title", Query = "q", TopK = 5 },
            writer, TestServerCallContext.Create());

        CapturedLimit(_vector).Should().Be(5);    // exactly top_k — no over-fetch
        await _vector.DidNotReceive().RetrieveNamedVectorAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<ulong>>(), Arg.Any<string>());

        // Still re-ranked, and bit-for-bit what the ungated code returned: base cosines, in order.
        written.Should().HaveCount(5);
        written.Select(w => w.Score).Should()
               .Equal(0.99f, 0.98f, 0.97f, 0.96f, 0.95f);
    }

    // Only ONE signal absent is not enough: an embedding-only property on a schema that DOES
    // carry a decay field still re-ranks non-trivially, so the 4x over-fetch stays.
    [Fact]
    public async Task SearchSimilar_NoCentroidButDecayFieldPresent_StillOverFetchesFourTimesTopK()
    {
        await _registry.RegisterAsync(EmbeddingOnlyWithDecaySchema());

        var fakeVector = UnitVector();
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(fakeVector);

        var results = Enumerable.Range(1, 8)
            .Select(i => new VectorSearchResult((ulong)i, 1.0 - i * 0.01,
                new Dictionary<string, string> { ["title"] = $"a{i}" }))
            .ToList();
        _vector.SearchNamedAsync("dated_test-tenant", "title_vector", fakeVector, Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(results.AsReadOnly());

        var (writer, written) = MakeStream<SearchResponse>();
        await _sut.SearchSimilar(
            new SearchSimilarRequest { TypeName = "Dated", Property = "Title", Query = "q", TopK = 5 },
            writer, TestServerCallContext.Create());

        CapturedLimit(_vector).Should().Be(20);   // 4 × top_k
        written.Should().HaveCount(5);
    }

    // ── Result diversification (MMR) ────────────────────────────────────────────

    private static float[] OrthogonalUnitVector()
    {
        var v = new float[768];
        v[1] = 1f;
        return v;
    }

    // Hand-computed MMR (lambda = 0.70) over 3 dual-annotated candidates, query = e0:
    //   A: BaseScore=1.00, centroid=e0 (sim to query=1.0)  → fused = (0.6*1.00 + 0.3*1.0)/0.9 = 1.0000
    //   B: BaseScore=0.85, centroid=e0 (sim to query=1.0)  → fused = (0.6*0.85 + 0.3*1.0)/0.9 = 0.9000
    //   C: BaseScore=1.00, centroid=e1 (sim to query=0.0)  → fused = (0.6*1.00 + 0.3*0.0)/0.9 = 0.6667
    // Fused-descending order fed to the diversifier: [A, B, C].
    // Step 1 selects A unconditionally (highest fused).
    // A's centroid is e0, same as B's (cosine(B,A) = 1.0) and orthogonal to C's (cosine(C,A) = 0.0).
    //   Mmr(B) = 0.7*0.9000 - 0.3*1.0 = 0.33
    //   Mmr(C) = 0.7*0.6667 - 0.3*0.0 = 0.4667
    // C's MMR score beats B's, so C is selected second despite its materially lower fused score —
    // B (the near-duplicate of the already-selected A) is passed over.
    [Fact]
    public async Task SearchSimilar_PromotesDissimilarCandidate_OverNearDuplicate_DespiteLowerFusedScore()
    {
        await _registry.RegisterAsync(DualAnnotatedSchema());

        var queryVector = UnitVector(); // e0
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(queryVector);

        var results = new List<VectorSearchResult>
        {
            new(1, 1.00, new Dictionary<string, string> { ["body"] = "A" }),
            new(2, 0.85, new Dictionary<string, string> { ["body"] = "B-near-duplicate" }),
            new(3, 1.00, new Dictionary<string, string> { ["body"] = "C-dissimilar" }),
        };
        _vector.SearchNamedAsync("docs_test-tenant", "body_vector", queryVector, Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(results.AsReadOnly());

        var centroids = new Dictionary<ulong, float[]>
        {
            [1] = UnitVector(),           // A: e0
            [2] = UnitVector(),           // B: e0 — near-duplicate of A (cosine ≈ 1.0)
            [3] = OrthogonalUnitVector(), // C: e1 — dissimilar from A (cosine ≈ 0.0)
        };
        _vector.RetrieveNamedVectorAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<ulong>>(), Arg.Any<string>())
               .Returns((IReadOnlyDictionary<ulong, float[]>)centroids);

        var (writer, written) = MakeStream<SearchResponse>();
        await _sut.SearchSimilar(
            new SearchSimilarRequest { TypeName = "Doc", Property = "Body", Query = "q", TopK = 2 },
            writer, TestServerCallContext.Create());

        written.Should().HaveCount(2);
        written[0].Data.Fields["Body"].StringValue.Should().Be("A");
        written[0].Score.Should().BeApproximately(1.0f, 0.001f);
        written[1].Data.Fields["Body"].StringValue.Should().Be("C-dissimilar");
        written[1].Score.Should().BeApproximately(0.6667f, 0.001f);
    }

    // Embedding-only property: no centroid signal exists at all, so every candidate's
    // DiversityVector is null and the diversifier degrades to the fused-descending order —
    // i.e. behaves identically to the plain Take(topK) it replaced.
    [Fact]
    public async Task SearchSimilar_EmbeddingOnlyProperty_ResultsUnchangedFromFusedOrder()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var fakeVector = UnitVector();
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(fakeVector);

        var results = Enumerable.Range(1, 5)
            .Select(i => new VectorSearchResult((ulong)i, 1.0 - i * 0.01,
                new Dictionary<string, string> { ["title"] = $"a{i}" }))
            .ToList();
        _vector.SearchNamedAsync("articles_test-tenant", "title_vector", fakeVector, Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(results.AsReadOnly());

        var (writer, written) = MakeStream<SearchResponse>();
        await _sut.SearchSimilar(
            new SearchSimilarRequest { TypeName = "Article", Property = "Title", Query = "q", TopK = 5 },
            writer, TestServerCallContext.Create());

        written.Should().HaveCount(5);
        written.Select(w => w.Score).Should()
               .Equal(0.99f, 0.98f, 0.97f, 0.96f, 0.95f);
    }

    // Part 3 already fetches the object's own centroid once via RetrieveNamedVectorAsync;
    // diversification must consume that same fetch and issue no additional Qdrant retrieve.
    [Fact]
    public async Task SearchSimilar_Diversification_IssuesNoAdditionalRetrieve()
    {
        await _registry.RegisterAsync(DualAnnotatedSchema());

        var fakeVector = UnitVector();
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(fakeVector);

        var results = Enumerable.Range(1, 8)
            .Select(i => new VectorSearchResult((ulong)i, 1.0 - i * 0.01,
                new Dictionary<string, string> { ["body"] = $"a{i}" }))
            .ToList();
        _vector.SearchNamedAsync("docs_test-tenant", "body_vector", fakeVector, Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(results.AsReadOnly());
        _vector.RetrieveNamedVectorAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<ulong>>(), Arg.Any<string>())
               .Returns((IReadOnlyDictionary<ulong, float[]>)new Dictionary<ulong, float[]>());

        var (writer, _) = MakeStream<SearchResponse>();
        await _sut.SearchSimilar(
            new SearchSimilarRequest { TypeName = "Doc", Property = "Body", Query = "q", TopK = 5 },
            writer, TestServerCallContext.Create());

        await _vector.Received(1).RetrieveNamedVectorAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<ulong>>(), Arg.Any<string>());
    }

    // SearchChunks only accepts an [IversonChunk] property, and every chunk field gets a
    // "<property>_centroid" on the object collection — the centroid signal is ALWAYS possible
    // here, so the identity gate can never fire and the 4x over-fetch always stands.
    [Fact]
    public async Task SearchChunks_OverFetchesFourTimesTopK_AndTrimsToTopK()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var fakeVector = UnitVector();
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(fakeVector);

        var results = Enumerable.Range(1, 12)
            .Select(i => new VectorSearchResult((ulong)i, 1.0 - i * 0.01,
                new Dictionary<string, string> { ["text"] = $"c{i}", ["parent_id"] = Guid.NewGuid().ToString() }))
            .ToList();
        _vector.SearchNamedAsync("articles_chunks_test-tenant", "body_vector", fakeVector, Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(results.AsReadOnly());

        var (writer, written) = MakeStream<ChunkSearchResponse>();
        await _sut.SearchChunks(
            new SearchChunksRequest { TypeName = "Article", Property = "Body", Query = "q", TopK = 3 },
            writer, TestServerCallContext.Create());

        CapturedLimit(_vector).Should().Be(12);
        written.Should().HaveCount(3);
    }

    [Fact]
    public async Task SearchChunks_BatchesCentroidRetrieve_ToDistinctParentIds()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var fakeVector = UnitVector();
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(fakeVector);

        var sharedParent = Guid.NewGuid().ToString();
        var results = Enumerable.Range(1, 3)
            .Select(i => new VectorSearchResult((ulong)i, 0.9 - i * 0.01,
                new Dictionary<string, string> { ["text"] = $"c{i}", ["parent_id"] = sharedParent }))
            .ToList();
        _vector.SearchNamedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(results.AsReadOnly());

        string? capturedCollection = null, capturedVectorName = null;
        List<ulong>? capturedIds = null;
        _vector.RetrieveNamedVectorAsync(
                   Arg.Is<string>(c => c == "articles_test-tenant"), Arg.Any<IReadOnlyList<ulong>>(), Arg.Any<string>())
               .Returns(ci =>
               {
                   capturedCollection = (string)ci[0]!;
                   capturedIds        = ((IReadOnlyList<ulong>)ci[1]!).ToList();
                   capturedVectorName = (string)ci[2]!;
                   return (IReadOnlyDictionary<ulong, float[]>)new Dictionary<ulong, float[]>();
               });
        _vector.RetrieveNamedVectorAsync(
                   Arg.Is<string>(c => c == "articles_chunks_test-tenant"), Arg.Any<IReadOnlyList<ulong>>(), Arg.Any<string>())
               .Returns((IReadOnlyDictionary<ulong, float[]>)new Dictionary<ulong, float[]>());

        var (writer, written) = MakeStream<ChunkSearchResponse>();
        await _sut.SearchChunks(
            new SearchChunksRequest { TypeName = "Article", Property = "Body", Query = "q", TopK = 5 },
            writer, TestServerCallContext.Create());

        await _vector.Received(1).RetrieveNamedVectorAsync(
            Arg.Is<string>(c => c == "articles_test-tenant"), Arg.Any<IReadOnlyList<ulong>>(), Arg.Any<string>());
        capturedIds.Should().ContainSingle();                       // three chunks, one parent
        capturedCollection.Should().Be("articles_test-tenant");     // the OBJECT collection
        capturedVectorName.Should().Be("body_centroid");
        written.Should().HaveCount(3);
    }

    [Fact]
    public async Task SearchChunks_CentroidRetrieveThrows_KeepsRawCosineOrder()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var fakeVector = UnitVector();
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(fakeVector);

        var results = new List<VectorSearchResult>
        {
            new(1, 0.90, new Dictionary<string, string> { ["text"] = "hi", ["parent_id"] = Guid.NewGuid().ToString() }),
            new(2, 0.50, new Dictionary<string, string> { ["text"] = "lo", ["parent_id"] = Guid.NewGuid().ToString() })
        };
        _vector.SearchNamedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(results.AsReadOnly());
        _vector.RetrieveNamedVectorAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<ulong>>(), Arg.Any<string>())
               .Returns<Task<IReadOnlyDictionary<ulong, float[]>>>(_ => throw new InvalidOperationException("qdrant down"));

        var (writer, written) = MakeStream<ChunkSearchResponse>();
        await _sut.SearchChunks(
            new SearchChunksRequest { TypeName = "Article", Property = "Body", Query = "q", TopK = 5 },
            writer, TestServerCallContext.Create());

        // Degraded, not failed: every centroid absent, so the fused score is the raw cosine.
        written.Should().HaveCount(2);
        written[0].ChunkText.Should().Be("hi");
        written[0].Score.Should().BeApproximately(0.90f, 0.0001f);
        written[1].Score.Should().BeApproximately(0.50f, 0.0001f);
    }

    [Fact]
    public async Task SearchSimilar_EmbeddingOnlyProperty_DoesNotFetchCentroids()
    {
        // Article.Title carries [IversonEmbedding] but not [IversonChunk] — no title_centroid exists.
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var fakeVector = UnitVector();
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(fakeVector);
        _vector.SearchNamedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(new List<VectorSearchResult>
               {
                   new(7, 0.42, new Dictionary<string, string> { ["title"] = "t" })
               }.AsReadOnly());

        var (writer, written) = MakeStream<SearchResponse>();
        await _sut.SearchSimilar(
            new SearchSimilarRequest { TypeName = "Article", Property = "Title", Query = "q", TopK = 5 },
            writer, TestServerCallContext.Create());

        await _vector.DidNotReceive().RetrieveNamedVectorAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<ulong>>(), Arg.Any<string>());
        written.Should().ContainSingle();
        written[0].Score.Should().BeApproximately(0.42f, 0.0001f);   // untouched base cosine
    }

    [Fact]
    public async Task SearchSimilar_DualAnnotatedProperty_FetchesCentroids_AndFusedScoreReachesResponse()
    {
        await _registry.RegisterAsync(DualAnnotatedSchema());

        var fakeVector = UnitVector();
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(fakeVector);
        _vector.SearchNamedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(new List<VectorSearchResult>
               {
                   new(11, 0.50, new Dictionary<string, string> { ["body"] = "b" })
               }.AsReadOnly());

        string? capturedVectorName = null;
        _vector.RetrieveNamedVectorAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<ulong>>(), Arg.Any<string>())
               .Returns(ci =>
               {
                   capturedVectorName = (string)ci[2]!;
                   return (IReadOnlyDictionary<ulong, float[]>)new Dictionary<ulong, float[]>
                   {
                       [11] = UnitVector()   // identical to the query vector → cosine 1.0
                   };
               });

        var (writer, written) = MakeStream<SearchResponse>();
        await _sut.SearchSimilar(
            new SearchSimilarRequest { TypeName = "Doc", Property = "Body", Query = "q", TopK = 5 },
            writer, TestServerCallContext.Create());

        capturedVectorName.Should().Be("body_centroid");
        written.Should().ContainSingle();
        // (0.60 × 0.50 + 0.30 × 1.00) / 0.90 — no decay column on this schema.
        written[0].Score.Should().BeApproximately(0.6667f, 0.0005f);
    }

    [Fact]
    public async Task SearchChunks_WhenRerankPermutesOrder_EachResponseKeepsItsOwnTextParentAndScore()
    {
        // Guards the re-join: the fused ranking here is a genuine PERMUTATION of the order Qdrant
        // returned, so a positional re-join (ranked[i] paired with results[i]) would emit chunk A's
        // text and parent alongside chunk B's score. Ranking arithmetic (no decay column on Article):
        //   A: base 0.90, parent centroid orthogonal to the query → cos 0.0 → (0.6×0.90 + 0.3×0.0)/0.9 = 0.6000
        //   B: base 0.50, parent centroid identical to the query  → cos 1.0 → (0.6×0.50 + 0.3×1.0)/0.9 = 0.6667
        // so B outranks A despite the lower base cosine, reversing the search order.
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var query = UnitVector();                     // e0
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(query);

        var parentA = Guid.NewGuid().ToString();
        var parentB = Guid.NewGuid().ToString();

        var results = new List<VectorSearchResult>
        {
            new(1, 0.90, new Dictionary<string, string> { ["text"] = "text-A", ["parent_id"] = parentA }),
            new(2, 0.50, new Dictionary<string, string> { ["text"] = "text-B", ["parent_id"] = parentB })
        };
        _vector.SearchNamedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(results.AsReadOnly());

        var orthogonal = new float[768];
        orthogonal[1] = 1f;                           // e1 → cosine 0 against e0

        var idA = InvokeKeyToUlong(parentA);
        var idB = InvokeKeyToUlong(parentB);
        _vector.RetrieveNamedVectorAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<ulong>>(), Arg.Any<string>())
               .Returns((IReadOnlyDictionary<ulong, float[]>)new Dictionary<ulong, float[]>
               {
                   [idA] = orthogonal,
                   [idB] = UnitVector()
               });

        var (writer, written) = MakeStream<ChunkSearchResponse>();
        await _sut.SearchChunks(
            new SearchChunksRequest { TypeName = "Article", Property = "Body", Query = "q", TopK = 5 },
            writer, TestServerCallContext.Create());

        written.Should().HaveCount(2);

        // B first, carrying ITS OWN text/parent — a positional join would put "text-A"/parentA here.
        written[0].ChunkText.Should().Be("text-B");
        written[0].ParentKey.Should().Be(parentB);
        written[0].Score.Should().BeApproximately(0.6667f, 0.0005f);

        written[1].ChunkText.Should().Be("text-A");
        written[1].ParentKey.Should().Be(parentA);
        written[1].Score.Should().BeApproximately(0.6000f, 0.0005f);
    }

    // ── SearchChunks diversification (chunk-level, not parent-level) ───────────

    // The chunk-vector retrieve is a SECOND round trip, distinct from part 3's parent-centroid
    // retrieve: different collection ("chunks" vs the object collection), different vector name
    // (the plain "<property>_vector" vs "<property>_centroid").
    [Fact]
    public async Task SearchChunks_Diversification_FetchesBothParentCentroidsAndChunkVectors()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var fakeVector = UnitVector();
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(fakeVector);

        var parent = Guid.NewGuid().ToString();
        var results = new List<VectorSearchResult>
        {
            new(1, 0.90, new Dictionary<string, string> { ["text"] = "c1", ["parent_id"] = parent }),
            new(2, 0.80, new Dictionary<string, string> { ["text"] = "c2", ["parent_id"] = parent }),
        };
        _vector.SearchNamedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(results.AsReadOnly());
        _vector.RetrieveNamedVectorAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<ulong>>(), Arg.Any<string>())
               .Returns((IReadOnlyDictionary<ulong, float[]>)new Dictionary<ulong, float[]>());

        var (writer, written) = MakeStream<ChunkSearchResponse>();
        await _sut.SearchChunks(
            new SearchChunksRequest { TypeName = "Article", Property = "Body", Query = "q", TopK = 2 },
            writer, TestServerCallContext.Create());

        await _vector.Received(1).RetrieveNamedVectorAsync(
            "articles_test-tenant", Arg.Any<IReadOnlyList<ulong>>(), "body_centroid");
        await _vector.Received(1).RetrieveNamedVectorAsync(
            "articles_chunks_test-tenant", Arg.Any<IReadOnlyList<ulong>>(), "body_vector");
        written.Should().HaveCount(2);
    }

    // With topK = 1, MMR's selection loop never runs, so the chunk-vector retrieve provably cannot
    // change the returned set or its order — it is skipped. The parent-centroid retrieve still
    // fires, because it feeds the fused score that decides WHICH single chunk wins.
    [Fact]
    public async Task SearchChunks_TopKOne_SkipsChunkVectorRetrieveButStillFetchesParentCentroids()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var fakeVector = UnitVector();
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(fakeVector);

        var parent = Guid.NewGuid().ToString();
        var results = new List<VectorSearchResult>
        {
            new(1, 0.90, new Dictionary<string, string> { ["text"] = "c1", ["parent_id"] = parent }),
            new(2, 0.80, new Dictionary<string, string> { ["text"] = "c2", ["parent_id"] = parent }),
        };
        _vector.SearchNamedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(results.AsReadOnly());
        _vector.RetrieveNamedVectorAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<ulong>>(), Arg.Any<string>())
               .Returns((IReadOnlyDictionary<ulong, float[]>)new Dictionary<ulong, float[]>());

        var (writer, written) = MakeStream<ChunkSearchResponse>();
        await _sut.SearchChunks(
            new SearchChunksRequest { TypeName = "Article", Property = "Body", Query = "q", TopK = 1 },
            writer, TestServerCallContext.Create());

        await _vector.Received(1).RetrieveNamedVectorAsync(
            "articles_test-tenant", Arg.Any<IReadOnlyList<ulong>>(), "body_centroid");
        await _vector.DidNotReceive().RetrieveNamedVectorAsync(
            "articles_chunks_test-tenant", Arg.Any<IReadOnlyList<ulong>>(), Arg.Any<string>());
        written.Should().HaveCount(1);
    }

    // Three chunks share ONE parent, so a parent-level diversity signal (identical centroid for
    // all three) cannot distinguish them at all — whichever ranks second by fused score would be
    // picked mechanically, with no actual diversity effect. Chunk-level vectors tell a different
    // story: A and B are near-identical passages (same chunk vector) while C is dissimilar from
    // both (orthogonal chunk vector), even though all three came from the same document.
    //
    // Query = e0. Parent centroid = e0 for all three (cos to query = 1.0) — identical, so it
    // contributes the same +0.3 term to every fused score and never changes relative order.
    //   A: base=0.95 → fused = (0.6*0.95 + 0.3*1.0)/0.9 = 0.9667
    //   B: base=0.90 → fused = (0.6*0.90 + 0.3*1.0)/0.9 = 0.9333
    //   C: base=0.85 → fused = (0.6*0.85 + 0.3*1.0)/0.9 = 0.9000
    // Fused-descending order fed to the diversifier: [A, B, C].
    // Step 1 selects A unconditionally (highest fused).
    // A's CHUNK vector is e0 — identical to B's chunk vector (cos(B,A) = 1.0) and orthogonal to
    // C's chunk vector (cos(C,A) = 0.0).
    //   Mmr(B) = 0.7*0.9333 - 0.3*1.0 = 0.3533
    //   Mmr(C) = 0.7*0.9000 - 0.3*0.0 = 0.6300
    // C's MMR score beats B's, so with top_k=2 the selection is [A, C]: the near-duplicate B
    // (same parent AND a near-identical passage) is suppressed, while C (same parent but a
    // dissimilar passage) is NOT suppressed — proving the diversity signal operates at chunk
    // granularity, not parent granularity.
    [Fact]
    public async Task SearchChunks_SuppressesNearDuplicatePassage_ButNotDissimilarPassage_EvenSharingOneParent()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var queryVector = UnitVector(); // e0
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(queryVector);

        var sharedParent = Guid.NewGuid().ToString();
        var results = new List<VectorSearchResult>
        {
            new(1, 0.95, new Dictionary<string, string> { ["text"] = "A",              ["parent_id"] = sharedParent }),
            new(2, 0.90, new Dictionary<string, string> { ["text"] = "B-near-duplicate", ["parent_id"] = sharedParent }),
            new(3, 0.85, new Dictionary<string, string> { ["text"] = "C-dissimilar",     ["parent_id"] = sharedParent }),
        };
        _vector.SearchNamedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(results.AsReadOnly());

        var parentUlong = InvokeKeyToUlong(sharedParent);
        _vector.RetrieveNamedVectorAsync(
                   Arg.Is<string>(c => c == "articles_test-tenant"), Arg.Any<IReadOnlyList<ulong>>(), Arg.Any<string>())
               .Returns((IReadOnlyDictionary<ulong, float[]>)new Dictionary<ulong, float[]>
               {
                   [parentUlong] = UnitVector(), // same parent centroid (e0) for all three chunks
               });
        _vector.RetrieveNamedVectorAsync(
                   Arg.Is<string>(c => c == "articles_chunks_test-tenant"), Arg.Any<IReadOnlyList<ulong>>(), Arg.Any<string>())
               .Returns((IReadOnlyDictionary<ulong, float[]>)new Dictionary<ulong, float[]>
               {
                   [1] = UnitVector(),           // A: e0
                   [2] = UnitVector(),           // B: e0 — near-duplicate of A (cosine ≈ 1.0)
                   [3] = OrthogonalUnitVector(), // C: e1 — dissimilar from A (cosine ≈ 0.0)
               });

        var (writer, written) = MakeStream<ChunkSearchResponse>();
        await _sut.SearchChunks(
            new SearchChunksRequest { TypeName = "Article", Property = "Body", Query = "q", TopK = 2 },
            writer, TestServerCallContext.Create());

        written.Should().HaveCount(2);
        written[0].ChunkText.Should().Be("A");
        written[0].Score.Should().BeApproximately(0.9667f, 0.001f);
        written[1].ChunkText.Should().Be("C-dissimilar");
        written[1].Score.Should().BeApproximately(0.9000f, 0.001f);
    }

    // A failed chunk-vector retrieve must degrade selection, never fail the search: every
    // diversity vector becomes absent, and the diversifier falls back to the fused-descending
    // order — bit-for-bit what the plain Take(topK) it replaced would have returned. The
    // parent-centroid retrieve is stubbed to SUCCEED here so the test isolates the chunk-vector
    // failure path specifically, rather than duplicating the "both retrieves throw" coverage
    // already provided by SearchChunks_CentroidRetrieveThrows_KeepsRawCosineOrder.
    [Fact]
    public async Task SearchChunks_ChunkVectorRetrieveThrows_KeepsFusedOrder_AndStreamsExactlyTopK()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var fakeVector = UnitVector();
        _embedding.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(fakeVector);

        var results = Enumerable.Range(1, 5)
            .Select(i => new VectorSearchResult((ulong)i, 1.0 - i * 0.1,
                new Dictionary<string, string> { ["text"] = $"c{i}" }))
            .ToList();
        _vector.SearchNamedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(results.AsReadOnly());

        _vector.RetrieveNamedVectorAsync(
                   Arg.Is<string>(c => c == "articles_test-tenant"), Arg.Any<IReadOnlyList<ulong>>(), Arg.Any<string>())
               .Returns((IReadOnlyDictionary<ulong, float[]>)new Dictionary<ulong, float[]>());
        _vector.RetrieveNamedVectorAsync(
                   Arg.Is<string>(c => c == "articles_chunks_test-tenant"), Arg.Any<IReadOnlyList<ulong>>(), Arg.Any<string>())
               .Returns<Task<IReadOnlyDictionary<ulong, float[]>>>(_ => throw new InvalidOperationException("qdrant down"));

        var (writer, written) = MakeStream<ChunkSearchResponse>();
        await _sut.SearchChunks(
            new SearchChunksRequest { TypeName = "Article", Property = "Body", Query = "q", TopK = 3 },
            writer, TestServerCallContext.Create());

        written.Should().HaveCount(3);
        written.Select(w => w.ChunkText).Should().Equal("c1", "c2", "c3");
        written.Select(w => w.Score).Should()
               .Equal(0.9f, 0.8f, 0.7f); // no centroid on these payloads either — raw cosine
    }

    // The point-id function is internal to IntelligenceStoreConsumer; the test needs the same
    // parent_id → ulong mapping the service applies so it can key the centroid map.
    private static ulong InvokeKeyToUlong(string key) =>
        Iverson.Api.Consumers.IntelligenceStoreConsumer.KeyToUlong(key);

    [Fact]
    public async Task SearchSimilar_NeverStreamsTheServerOwnedTenantColumn()
    {
        // Qdrant point payloads DO carry the tenant column by design (IntelligenceStoreConsumer
        // writes it as a discriminator alongside the collection routing), so this is a real leak
        // path: the payload is turned into the response Struct verbatim. The MaskDisallowedFields
        // call in the streaming loop is what closes it — and this schema has no FieldPermissions,
        // so AllowedFields is null and the strip must fire on the early-return path.
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var fakeVector = new float[768];
        _embedding.EmbedAsync("test query", Arg.Any<CancellationToken>()).Returns(fakeVector);

        var vectorResult = new VectorSearchResult(
            Id: 1, Score: 0.95,
            Payload: new Dictionary<string, string>
            {
                ["title"] = "Great Article",
                [SchemaDescriptor.TenantColumnName] = "test-tenant"
            });

        _vector.SearchNamedAsync("articles_test-tenant", "title_vector", fakeVector, Arg.Any<ulong>(), Arg.Any<Filter>())
               .Returns(new List<VectorSearchResult> { vectorResult }.AsReadOnly());

        var (writer, written) = MakeStream<SearchResponse>();
        await _sut.SearchSimilar(
            new SearchSimilarRequest { TypeName = "Article", Property = "Title", Query = "test query", TopK = 5 },
            writer, TestServerCallContext.Create());

        written.Should().HaveCount(1);
        written[0].Data.Fields.Should().NotContainKey(SchemaDescriptor.TenantColumnName);
        written[0].Data.Fields.Should().ContainKey("Title"); // the rest of the payload survives
    }

    // ── Result-side strip for the three streaming SQL RPCs ────────────────────
    //
    // Search, GroupBy and Pipeline never reach MaskDisallowedFields — they stream the row
    // dictionary StarRocks returned, essentially verbatim (Search additionally drops non-allowed
    // keys, but only when AllowedFields is non-null). Their entire protection against the
    // server-owned tenant column was therefore SQL-side, in the query builders.
    //
    // The joined-type wildcard bug (`Type`.* over a physical table, which ColumnsFor could not
    // reach) is empirical proof that a single-point SQL defence on this path is bypassable, so
    // these three tests pin a second, result-side line of defence. Each supplies a row that
    // ALREADY carries the column — i.e. it assumes the SQL-side exclusion has failed — and
    // asserts the RPC still does not put it on the wire.
    //
    // Keyed on the reserved __TenantId spelling, not schema.TenantColumn: a legacy schema whose
    // boundary sits on a client-declared column has always exposed that name as part of the
    // client's own contract.

    private static Dictionary<string, object> RowWithTenantColumn() => new()
    {
        ["Name"] = "Alice",
        [SchemaDescriptor.TenantColumnName] = "test-tenant"
    };

    [Fact]
    public async Task Search_StripsTheServerOwnedTenantColumnFromStreamedRows()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        _search.SearchAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Any<Func<string, EngagementQuerySchema?>?>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns(new[] { (dynamic)RowWithTenantColumn() }.AsEnumerable());

        var (writer, written) = MakeStream<SearchResponse>();
        await _sut.Search(new SearchRequest { TypeName = "Author" }, writer, TestServerCallContext.Create());

        written.Should().HaveCount(1);
        written[0].Data.Fields.Should().NotContainKey(SchemaDescriptor.TenantColumnName);
        written[0].Data.Fields.Should().ContainKey("Name");
    }

    [Fact]
    public async Task GroupBy_StripsTheServerOwnedTenantColumnFromStreamedRows()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        _search.GroupByAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<GroupByRequest>(),
                Arg.Any<Func<string, EngagementQuerySchema?>>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns(new[] { (dynamic)RowWithTenantColumn() }.AsEnumerable());

        var request = new GroupByRequest { TypeName = "Author", Keys = { "Name" } };
        request.Metrics.Add(new MetricSpec { Name = "cnt", Type = AggregationType.Count });

        var (writer, written) = MakeStream<SearchResponse>();
        await _sut.GroupBy(request, writer, TestServerCallContext.Create());

        written.Should().HaveCount(1);
        written[0].Data.Fields.Should().NotContainKey(SchemaDescriptor.TenantColumnName);
        written[0].Data.Fields.Should().ContainKey("Name");
    }

    [Fact]
    public async Task Pipeline_StripsTheServerOwnedTenantColumnFromStreamedRows()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        _search.PipelineAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<PipelineRequest>(),
                Arg.Any<Func<string, EngagementQuerySchema?>>(),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns(new[] { (dynamic)RowWithTenantColumn() }.AsEnumerable());

        var (writer, written) = MakeStream<SearchResponse>();
        await _sut.Pipeline(
            new PipelineRequest { TypeName = "Author" }, writer, TestServerCallContext.Create());

        written.Should().HaveCount(1);
        written[0].Data.Fields.Should().NotContainKey(SchemaDescriptor.TenantColumnName);
        written[0].Data.Fields.Should().ContainKey("Name");
    }
}
