using FluentAssertions;
using Google.Protobuf;
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
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using SchemaRelationDescriptor = Iverson.Api.Schema.RelationDescriptor;
using SchemaRelationKind       = Iverson.Api.Schema.RelationKind;
using SrAggResult              = Iverson.StarRocks.AggregationResult;

namespace Iverson.Api.Tests.Grpc;

/// <summary>
/// CSR round 11 #20: a type registered to another tenant is answered exactly as a type nobody
/// registered, on every data RPC, so a caller cannot use a type name to learn that another tenant
/// has it. The table below is spec §5a's: each row runs the same request against an unregistered
/// name and a foreign-owned one and expects one answer, written once as a function of the name the
/// request carried.
/// <para>
/// The foreign type is registered as "Ledger" and requested as "ledger": the registry matches
/// names case-insensitively, so an answer that echoed the registered spelling would show here.
/// Its table holds a row the caller's tenant can read, so a call that reached the store would
/// answer differently and the comparison could not pass by accident.
/// </para>
/// </summary>
public sealed class OwnerPrivateTypeVisibilityTests : IAsyncLifetime
{
    private const string Unregistered = "ghost";
    private const string Foreign      = "ledger";
    private const string Key          = "33333333-0000-0000-0000-000000000003";
    private const string MalformedKey = "not-a-guid";
    private const string InvoiceId    = "44444444-0000-0000-0000-000000000004";
    private const string TraceId      = "t";

    // Readable by the default acting user's tenant ("test-tenant").
    private static readonly string LedgerJson = $$"""{"Id":"{{Key}}","Name":"Ledger row","TenantId":"test-tenant"}""";

    private readonly IRecordStoreQueryExecutor _sql = Substitute.For<IRecordStoreQueryExecutor>();
    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();
    private readonly IOutboxWriter _outboxWriter = Substitute.For<IOutboxWriter>();
    private readonly IOutboxPublisher _outboxPublisher = Substitute.For<IOutboxPublisher>();
    private readonly IEngagementStoreSearchService _search = Substitute.For<IEngagementStoreSearchService>();
    private readonly IVectorQueryService _vector = Substitute.For<IVectorQueryService>();
    private readonly IEmbeddingServiceResolver _resolver = Substitute.For<IEmbeddingServiceResolver>();
    private readonly IRowFieldAuthorizationEvaluator _authEvaluator = new RowFieldAuthorizationEvaluator();
    private readonly ActingUserAccessor _actingUserAccessor = new()
        { ActingUser = ActingUserFixtures.Principal("test-user", "test-bypass") };
    private readonly SchemaRegistry _registry;
    private readonly ObjectMappingGrpcService _mapping;
    private readonly ObjectPersistenceGrpcService _persistence;
    private readonly ObjectRetrievalGrpcService _retrieval;
    private readonly ObjectSearchGrpcService _searchService;

    public OwnerPrivateTypeVisibilityTests()
    {
        _sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);
        _registry = new SchemaRegistry(new SchemaRegistryRepository(_sql), NullLogger<SchemaRegistry>.Instance);

        _entities
            .FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<EntityAccess>())
            .Returns((string?)null);
        _entities
            .FetchByKeyAsync(Arg.Is<TableSchema>(s => s.TableName == "ledgers"), Arg.Any<string>(), Arg.Any<EntityAccess>())
            .Returns(LedgerJson);
        _entities
            .FetchManyByKeysAsync(Arg.Is<TableSchema>(s => s.TableName == "ledgers"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<EntityAccess>())
            .Returns(new[] { new KeyedRow(Key, LedgerJson) });
        _entities
            .FetchByColumnAsync(Arg.Is<TableSchema>(s => s.TableName == "ledgers"), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EntityAccess>())
            .Returns(new[] { LedgerJson });

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

        var auditLog = new AuditLog(NullLogger<AuditLog>.Instance);
        _mapping = new ObjectMappingGrpcService(
            _entities,
            Substitute.For<IRecordStoreTransactionRunner>(),
            _outboxPublisher,
            _registry,
            new RelationValidator(),
            new PayloadSizeValidator(),
            new EntityKeyAccessor(),
            _outboxWriter,
            NullLogger<ObjectMappingGrpcService>.Instance,
            _actingUserAccessor,
            _authEvaluator,
            new EntityRelationResolver(_registry, _entities, _authEvaluator),
            Substitute.For<ISchemaRegistrationOrchestrator>(),
            auditLog,
            EngagementQueryLimitOptions.Default);
        _persistence = new ObjectPersistenceGrpcService(
            _outboxPublisher,
            _registry,
            new RelationValidator(),
            new PayloadSizeValidator(),
            new EntityKeyAccessor(),
            _outboxWriter,
            NullLogger<ObjectPersistenceGrpcService>.Instance,
            _entities,
            _actingUserAccessor,
            _authEvaluator,
            auditLog);
        _retrieval = new ObjectRetrievalGrpcService(
            _entities,
            _registry,
            NullLogger<ObjectRetrievalGrpcService>.Instance,
            _actingUserAccessor,
            _authEvaluator,
            auditLog);
        _searchService = new ObjectSearchGrpcService(
            _registry, _search, _vector, _resolver,
            NullLogger<ObjectSearchGrpcService>.Instance,
            _actingUserAccessor, _authEvaluator, new IntelligenceTenantScope("test-signing-key-0123456789abcdef"),
            new ResultReranker(Options.Create(new VectorRankingOptions())), new ResultDiversifier(),
            Options.Create(new VectorRankingOptions()),
            Options.Create(new DecayOptions()),
            Options.Create(new PopularitySignalOptions()),
            EngagementQueryLimitOptions.Default);
    }

    public async Task InitializeAsync()
    {
        // Unowned, as every SDK registration is: visible to every tenant.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        // Another tenant's type. "test-bypass" may read and write it, so only visibility stops the
        // default acting user (tenant "test-tenant") from using it.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema() with
        {
            TypeName = "Ledger", TableName = "ledgers", OwnerTenantId = "tenant-b"
        });
        // The default acting user's own tenant's type.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema() with
        {
            TypeName = "OwnLedger", TableName = "own_ledgers", OwnerTenantId = "test-tenant"
        });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Outcome capture ───────────────────────────────────────────────────────

    /// <summary>Status, detail and the response messages, as one comparable value.</summary>
    private sealed record Outcome(StatusCode? Code, string? Detail, string Responses);

    private static Outcome Completed(params IMessage[] responses) =>
        new(null, null, string.Join("\n", responses.Select(r => r.ToString())));

    private static Outcome Failed(StatusCode code, string detail) => new(code, detail, string.Empty);

    private static async Task<Outcome> UnaryAsync(Func<Task<IMessage>> call)
    {
        try
        {
            return Completed(await call());
        }
        catch (RpcException ex)
        {
            return Failed(ex.StatusCode, ex.Status.Detail);
        }
    }

    private static async Task<Outcome> StreamAsync<T>(Func<IServerStreamWriter<T>, Task> call) where T : IMessage
    {
        var stream = new FakeStream<T>();
        try
        {
            await call(stream);
            return Completed(stream.Written.Cast<IMessage>().ToArray());
        }
        catch (RpcException ex)
        {
            return Failed(ex.StatusCode, ex.Status.Detail);
        }
    }

    private sealed class FakeStream<T> : IServerStreamWriter<T>
    {
        public List<T> Written { get; } = [];
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(T message) { Written.Add(message); return Task.CompletedTask; }
        public Task WriteAsync(T message, CancellationToken cancellationToken)
            { Written.Add(message); return Task.CompletedTask; }
    }

    private static TestServerCallContext Ctx() => TestServerCallContext.Create();

    // ── Request and answer builders ───────────────────────────────────────────

    private static Struct Payload(params (string Key, string Value)[] fields)
    {
        var s = new Struct();
        foreach (var (k, v) in fields) s.Fields[k] = Value.ForString(v);
        return s;
    }

    private static JoinSpec JoinTo(string type) => new()
    {
        LeftType = "Author", RightType = type, LeftField = "Id", RightField = "Id", Kind = JoinKind.Inner
    };

    private static AggregateRequest AggregateRequestFor(string type, JoinSpec? join = null)
    {
        var request = new AggregateRequest { TypeName = type, TraceId = TraceId };
        request.Aggregations.Add(new AggregationSpec { Name = "by_name", Type = AggregationType.Terms, Field = "Name" });
        if (join is not null) request.Joins.Add(join);
        return request;
    }

    private static PipelineRequest PipelineJoining(string source)
    {
        var step = new PipelineStep { Name = "named" };
        var join = new PipelineJoin { Source = source, Kind = JoinKind.Inner };
        join.On.Add(new JoinCondition { Left = "Id", Right = "Id" });
        step.Joins.Add(join);
        step.Select.Add(new SelectItem { All = true });
        var request = new PipelineRequest { TypeName = "Author", TraceId = TraceId };
        request.Steps.Add(step);
        return request;
    }

    private static string NoSchema(string type) => $"No schema registered for '{type}'. Call RegisterSchema first.";

    private static Outcome MappingAbsent(string type, string key) => Completed(new MappingResponse
        { Success = false, Error = $"'{type}:{key}' not found.", TraceId = TraceId });

    private static Outcome DeleteAbsent(string type, string key) => Completed(new MappingDeleteResponse
        { Success = false, Error = $"'{type}:{key}' not found.", TraceId = TraceId });

    private static Outcome RowNotFound(string type) => Failed(StatusCode.NotFound, $"'{type}' row not found.");

    private static Outcome NotFoundResults(int count) => Completed(
        Enumerable.Repeat<IMessage>(new RetrievalResponse { Found = false, TraceId = TraceId }, count).ToArray());

    // ── The table (spec §5a) ──────────────────────────────────────────────────

    private sealed record RpcCase(
        Func<string, Task<Outcome>> Run, Func<string, Outcome> Expected, bool QueriesTheSearchStore = false);

    public static TheoryData<string> RpcsAnsweringAnInvisibleType() => new()
    {
        "Mapping.Get",
        "Mapping.Get with a malformed key",
        "Mapping.Get at depth 1",
        "Mapping.Delete",
        "Mapping.Delete with a malformed key",
        "Mapping.Update",
        "Mapping.Update with no key",
        "Mapping.Update with a malformed key",
        "Mapping.Update carrying the tenant column",
        "Persistence.Update",
        "Persistence.Update with no key",
        "Persistence.Update with a malformed key",
        "Mapping.Post",
        "Persistence.Post",
        "Retrieval.Get",
        "Retrieval.Get with a malformed key",
        "Retrieval.GetMany with a duplicate and a malformed key",
        "Search",
        "Aggregate",
        "GroupBy",
        "Pipeline",
        "SearchSimilar",
        "SearchChunks",
        "MatchPattern",
        "Search joining the type",
        "Aggregate joining the type",
        "GroupBy joining the type",
        "Pipeline joining the type",
    };

    private RpcCase CaseFor(string rpc) => rpc switch
    {
        "Mapping.Get" => new RpcCase(
            t => UnaryAsync(async () => await _mapping.Get(
                new MappingGetRequest { TypeName = t, Key = Key, TraceId = TraceId }, Ctx())),
            t => MappingAbsent(t, Key)),
        "Mapping.Get with a malformed key" => new RpcCase(
            t => UnaryAsync(async () => await _mapping.Get(
                new MappingGetRequest { TypeName = t, Key = MalformedKey, TraceId = TraceId }, Ctx())),
            t => MappingAbsent(t, MalformedKey)),
        "Mapping.Get at depth 1" => new RpcCase(
            t => UnaryAsync(async () => await _mapping.Get(
                new MappingGetRequest { TypeName = t, Key = Key, Depth = 1, TraceId = TraceId }, Ctx())),
            t => MappingAbsent(t, Key)),
        "Mapping.Delete" => new RpcCase(
            t => UnaryAsync(async () => await _mapping.Delete(
                new MappingDeleteRequest { TypeName = t, Key = Key, TraceId = TraceId }, Ctx())),
            t => DeleteAbsent(t, Key)),
        "Mapping.Delete with a malformed key" => new RpcCase(
            t => UnaryAsync(async () => await _mapping.Delete(
                new MappingDeleteRequest { TypeName = t, Key = MalformedKey, TraceId = TraceId }, Ctx())),
            t => DeleteAbsent(t, MalformedKey)),
        "Mapping.Update" => new RpcCase(
            t => UnaryAsync(async () => await _mapping.Update(
                new MappingWriteRequest { TypeName = t, Payload = Payload(("Id", Key), ("Name", "x")), TraceId = TraceId }, Ctx())),
            RowNotFound),
        "Mapping.Update with no key" => new RpcCase(
            t => UnaryAsync(async () => await _mapping.Update(
                new MappingWriteRequest { TypeName = t, Payload = Payload(("Name", "x")), TraceId = TraceId }, Ctx())),
            RowNotFound),
        "Mapping.Update with a malformed key" => new RpcCase(
            t => UnaryAsync(async () => await _mapping.Update(
                new MappingWriteRequest { TypeName = t, Payload = Payload(("Id", MalformedKey)), TraceId = TraceId }, Ctx())),
            RowNotFound),
        "Mapping.Update carrying the tenant column" => new RpcCase(
            t => UnaryAsync(async () => await _mapping.Update(
                new MappingWriteRequest
                {
                    TypeName = t,
                    Payload  = Payload(("Id", Key), (SchemaDescriptor.TenantColumnName, "other-tenant")),
                    TraceId  = TraceId
                }, Ctx())),
            RowNotFound),
        "Persistence.Update" => new RpcCase(
            t => UnaryAsync(async () => await _persistence.Update(
                new PersistRequest { TypeName = t, Payload = Payload(("Id", Key), ("Name", "x")), TraceId = TraceId }, Ctx())),
            RowNotFound),
        "Persistence.Update with no key" => new RpcCase(
            t => UnaryAsync(async () => await _persistence.Update(
                new PersistRequest { TypeName = t, Payload = Payload(("Name", "x")), TraceId = TraceId }, Ctx())),
            RowNotFound),
        "Persistence.Update with a malformed key" => new RpcCase(
            t => UnaryAsync(async () => await _persistence.Update(
                new PersistRequest { TypeName = t, Payload = Payload(("Id", MalformedKey)), TraceId = TraceId }, Ctx())),
            RowNotFound),
        "Mapping.Post" => new RpcCase(
            t => UnaryAsync(async () => await _mapping.Post(
                new MappingWriteRequest { TypeName = t, Payload = Payload(("Name", "x")), TraceId = TraceId }, Ctx())),
            t => Failed(StatusCode.FailedPrecondition, NoSchema(t))),
        "Persistence.Post" => new RpcCase(
            t => UnaryAsync(async () => await _persistence.Post(
                new PersistRequest { TypeName = t, Payload = Payload(("Name", "x")), TraceId = TraceId }, Ctx())),
            t => Failed(StatusCode.FailedPrecondition, NoSchema(t))),
        "Retrieval.Get" => new RpcCase(
            t => UnaryAsync(async () => await _retrieval.Get(
                new RetrievalRequest { TypeName = t, Key = Key, TraceId = TraceId }, Ctx())),
            _ => NotFoundResults(1)),
        "Retrieval.Get with a malformed key" => new RpcCase(
            t => UnaryAsync(async () => await _retrieval.Get(
                new RetrievalRequest { TypeName = t, Key = MalformedKey, TraceId = TraceId }, Ctx())),
            _ => NotFoundResults(1)),
        "Retrieval.GetMany with a duplicate and a malformed key" => new RpcCase(
            t => StreamAsync<RetrievalResponse>(w => _retrieval.GetMany(
                new RetrievalManyRequest { TypeName = t, Keys = { Key, Key, MalformedKey }, TraceId = TraceId }, w, Ctx())),
            _ => NotFoundResults(3)),
        "Search" => new RpcCase(
            t => StreamAsync<SearchResponse>(w => _searchService.Search(
                new SearchRequest { TypeName = t, TraceId = TraceId }, w, Ctx())),
            t => Failed(StatusCode.FailedPrecondition, NoSchema(t))),
        "Aggregate" => new RpcCase(
            t => UnaryAsync(async () => await _searchService.Aggregate(AggregateRequestFor(t), Ctx())),
            t => Failed(StatusCode.FailedPrecondition, NoSchema(t))),
        "GroupBy" => new RpcCase(
            t => StreamAsync<SearchResponse>(w => _searchService.GroupBy(
                new GroupByRequest { TypeName = t, Keys = { "Name" }, TraceId = TraceId }, w, Ctx())),
            t => Failed(StatusCode.FailedPrecondition, NoSchema(t))),
        "Pipeline" => new RpcCase(
            t => StreamAsync<SearchResponse>(w => _searchService.Pipeline(
                new PipelineRequest { TypeName = t, TraceId = TraceId }, w, Ctx())),
            t => Failed(StatusCode.FailedPrecondition, NoSchema(t))),
        "SearchSimilar" => new RpcCase(
            t => StreamAsync<SearchResponse>(w => _searchService.SearchSimilar(
                new SearchSimilarRequest { TypeName = t, Property = "Name", Query = "q", TraceId = TraceId }, w, Ctx())),
            t => Failed(StatusCode.FailedPrecondition, NoSchema(t))),
        "SearchChunks" => new RpcCase(
            t => StreamAsync<ChunkSearchResponse>(w => _searchService.SearchChunks(
                new SearchChunksRequest { TypeName = t, Property = "Bio", Query = "q", TraceId = TraceId }, w, Ctx())),
            t => Failed(StatusCode.FailedPrecondition, NoSchema(t))),
        "MatchPattern" => new RpcCase(
            t => StreamAsync<MatchPatternResponse>(w => _searchService.MatchPattern(
                new MatchPatternRequest
                {
                    TypeName = t, Pattern = "A", TraceId = TraceId, OrderBy = { new SearchSort { Property = "Id" } }
                }, w, Ctx())),
            t => Failed(StatusCode.FailedPrecondition, NoSchema(t))),
        "Search joining the type" => new RpcCase(
            t => StreamAsync<SearchResponse>(w => _searchService.Search(
                new SearchRequest { TypeName = "Author", Joins = { JoinTo(t) }, TraceId = TraceId }, w, Ctx())),
            t => Failed(StatusCode.FailedPrecondition, NoSchema(t))),
        "Aggregate joining the type" => new RpcCase(
            t => UnaryAsync(async () => await _searchService.Aggregate(AggregateRequestFor("Author", JoinTo(t)), Ctx())),
            t => Failed(StatusCode.FailedPrecondition, NoSchema(t))),
        "GroupBy joining the type" => new RpcCase(
            t => StreamAsync<SearchResponse>(w => _searchService.GroupBy(
                new GroupByRequest { TypeName = "Author", Keys = { "Name" }, Joins = { JoinTo(t) }, TraceId = TraceId }, w, Ctx())),
            t => Failed(StatusCode.FailedPrecondition, NoSchema(t))),
        // A pipeline source may name an earlier step, so an invisible source is dropped from the
        // authorization pass rather than refused; the builder answers it (asserted at builder
        // level in StarRocksPipelineBuilderTests, since _search is a mock here).
        "Pipeline joining the type" => new RpcCase(
            t => StreamAsync<SearchResponse>(w => _searchService.Pipeline(PipelineJoining(t), w, Ctx())),
            _ => Completed(),
            QueriesTheSearchStore: true),
        _ => throw new ArgumentOutOfRangeException(nameof(rpc), rpc, "No such row in the table.")
    };

    [Theory]
    [MemberData(nameof(RpcsAnsweringAnInvisibleType))]
    public async Task An_unregistered_type_and_another_tenants_type_get_the_same_answer(string rpc)
    {
        var rpcCase = CaseFor(rpc);

        var unregistered = await rpcCase.Run(Unregistered);
        var foreign      = await rpcCase.Run(Foreign);

        unregistered.Should().Be(rpcCase.Expected(Unregistered));
        foreign.Should().Be(rpcCase.Expected(Foreign));

        // Answered before any key is read and before any store is asked.
        _entities.ReceivedCalls().Should().BeEmpty();
        _outboxWriter.ReceivedCalls().Should().BeEmpty();
        _outboxPublisher.ReceivedCalls().Should().BeEmpty();
        _vector.ReceivedCalls().Should().BeEmpty();
        if (!rpcCase.QueriesTheSearchStore)
            _search.ReceivedCalls().Should().BeEmpty();
    }

    // ── The registry handed to every query builder ────────────────────────────

    [Fact]
    public async Task The_registry_every_query_builder_receives_resolves_only_types_the_caller_can_see()
    {
        var lookups = new List<Func<string, EngagementQuerySchema?>>();
        _search.SearchAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Do<Func<string, EngagementQuerySchema?>?>(f => lookups.Add(f!)),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns(Enumerable.Empty<dynamic>());
        _search.AggregateAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<SearchQuery?>(), Arg.Any<AggregationDescriptor>(),
                Arg.Any<SearchQuery?>(), Arg.Any<IReadOnlyList<JoinSpec>?>(),
                Arg.Do<Func<string, EngagementQuerySchema?>?>(f => lookups.Add(f!)),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns((SrAggResult?)null);
        _search.GroupByAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<GroupByRequest>(),
                Arg.Do<Func<string, EngagementQuerySchema?>>(lookups.Add),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns(Enumerable.Empty<dynamic>());
        _search.PipelineAsync(
                Arg.Any<EngagementQuerySchema>(), Arg.Any<PipelineRequest>(),
                Arg.Do<Func<string, EngagementQuerySchema?>>(lookups.Add),
                Arg.Any<IReadOnlyDictionary<string, AuthorizationConstraint>?>())
            .Returns(Enumerable.Empty<dynamic>());

        await StreamAsync<SearchResponse>(w => _searchService.Search(new SearchRequest { TypeName = "Author" }, w, Ctx()));
        await _searchService.Aggregate(AggregateRequestFor("Author"), Ctx());
        await StreamAsync<SearchResponse>(w => _searchService.GroupBy(
            new GroupByRequest { TypeName = "Author", Keys = { "Name" } }, w, Ctx()));
        await StreamAsync<SearchResponse>(w => _searchService.Pipeline(new PipelineRequest { TypeName = "Author" }, w, Ctx()));

        lookups.Should().HaveCount(4, "Search, Aggregate, GroupBy and Pipeline each hand the builder one");
        foreach (var lookup in lookups)
        {
            lookup(Unregistered).Should().BeNull();
            lookup(Foreign).Should().BeNull();
            lookup("Author").Should().NotBeNull();
            lookup("OwnLedger").Should().NotBeNull();
        }
    }

    // ── Relation hydration ────────────────────────────────────────────────────

    private static SchemaDescriptor InvoiceRelatedTo(SchemaRelationKind kind, string relatedType) =>
        SchemaFixtures.AuthorSchema() with
        {
            TypeName  = "Invoice",
            TableName = "invoices",
            Relations =
            [
                new SchemaRelationDescriptor("Target", kind, relatedType, kind switch
                {
                    SchemaRelationKind.ManyToMany => "TargetIds",
                    SchemaRelationKind.OneToMany  => "InvoiceId",
                    _                             => "TargetId"
                })
            ]
        };

    [Theory]
    [InlineData(SchemaRelationKind.ManyToOne)]
    [InlineData(SchemaRelationKind.ManyToMany)]
    [InlineData(SchemaRelationKind.OneToMany)]
    public async Task Relation_hydration_leaves_another_tenants_related_type_unresolved_like_an_unregistered_one(
        SchemaRelationKind kind)
    {
        var resolver = new EntityRelationResolver(_registry, _entities, _authEvaluator);
        var resolved = new List<Struct>();
        foreach (var relatedType in new[] { "Ghost", "Ledger" })
        {
            var invoice = JsonParser.Default.Parse<Struct>(
                $$"""{"Id":"{{InvoiceId}}","Name":"Inv","TargetId":"{{Key}}","TargetIds":["{{Key}}"],"TenantId":"test-tenant"}""");
            await resolver.ResolveRelationsAsync(
                invoice, InvoiceRelatedTo(kind, relatedType), depth: 1, _actingUserAccessor.ActingUser, CancellationToken.None);
            resolved.Add(invoice);
        }

        resolved[1].Should().Be(resolved[0]);
        resolved[0].Fields.Should().NotContainKey("Target");
        _entities.ReceivedCalls().Should().BeEmpty();
    }

    // ── What stays visible ────────────────────────────────────────────────────

    [Theory]
    [InlineData("test-tenant")]
    [InlineData("tenant-b")]
    public async Task An_unowned_type_stays_visible_to_every_tenant(string tenant)
    {
        _actingUserAccessor.ActingUser = ActingUserFixtures.PrincipalWithTenant("test-user", tenant, "test-bypass");
        _entities
            .FetchByKeyAsync(Arg.Is<TableSchema>(s => s.TableName == "authors"), Arg.Any<string>(), Arg.Any<EntityAccess>())
            .Returns($$"""{"Id":"{{Key}}","Name":"Alice","TenantId":"{{tenant}}"}""");

        var response = await _retrieval.Get(new RetrievalRequest { TypeName = "Author", Key = Key, TraceId = TraceId }, Ctx());

        response.Found.Should().BeTrue();
        response.Data.Fields["Name"].StringValue.Should().Be("Alice");
    }

    [Fact]
    public async Task An_owned_type_is_visible_to_its_own_tenant()
    {
        _actingUserAccessor.ActingUser = ActingUserFixtures.PrincipalWithTenant("test-user", "tenant-b", "test-bypass");
        _entities
            .FetchByKeyAsync(Arg.Is<TableSchema>(s => s.TableName == "ledgers"), Arg.Any<string>(), Arg.Any<EntityAccess>())
            .Returns($$"""{"Id":"{{Key}}","Name":"Ledger row","TenantId":"tenant-b"}""");

        var response = await _retrieval.Get(new RetrievalRequest { TypeName = Foreign, Key = Key, TraceId = TraceId }, Ctx());

        response.Found.Should().BeTrue();
        response.Data.Fields["Name"].StringValue.Should().Be("Ledger row");
    }
}
