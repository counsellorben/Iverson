using System.Security.Claims;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Authorization;
using Iverson.Api.Grpc;
using Iverson.Api.Reconciliation;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.Events;
using Iverson.Sql;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

public class ObjectMappingGrpcServiceTests
{
    private readonly IRecordStoreQueryExecutor _sql;
    private readonly IEntityRepository _entities;
    private readonly IRecordStoreTransactionRunner _txRunner;
    private readonly IRecordStoreSchemaManager _schemaManager;
    private readonly IEventProducer _events;
    private readonly SchemaRegistry _registry;
    private readonly IEmbeddingService _embedding;
    private readonly IActingUserAccessor _actingUserAccessor;
    private readonly IRowFieldAuthorizationEvaluator _authEvaluator = new RowFieldAuthorizationEvaluator();
    private readonly IOutboxPublisher _outboxPublisher;
    private readonly IEntityRelationResolver _relationResolver;
    private readonly ISchemaRegistrationOrchestrator _schemaRegistration;
    private readonly ILogger<AuditLog> _auditLogger = Substitute.For<ILogger<AuditLog>>();
    private readonly AuditLog _auditLog;
    private readonly ObjectMappingGrpcService _sut;

    private static readonly string AuthorId  = "11111111-0000-0000-0000-000000000001";
    private static readonly string ArticleId = "22222222-0000-0000-0000-000000000002";
    private static readonly string AuthorJson  = $$"""{"Id":"{{AuthorId}}","Name":"Alice","Bio":"Writer","TenantId":"test-tenant"}""";
    private static readonly string ArticleJson = $$"""{"Id":"{{ArticleId}}","Title":"Hello","Body":"World","AuthorId":"{{AuthorId}}","TenantId":"test-tenant"}""";

    public ObjectMappingGrpcServiceTests()
    {
        _sql      = Substitute.For<IRecordStoreQueryExecutor>();
        _entities = Substitute.For<IEntityRepository>();
        _events   = Substitute.For<IEventProducer>();

        _sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(1);
        _entities
            .FetchByColumnAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(Task.FromResult(Enumerable.Empty<string>()));
        // NSubstitute's auto-value for an unconfigured Task<string?> member is Task.FromResult(""),
        // not null — default every FetchByKeyAsync call to "row not found" so Update's new
        // pre-fetch (Task 6) doesn't try to JSON-parse an empty string in tests that don't care
        // about the pre-existing-row branch. Individual tests override this with .Returns(...)
        // for the specific TableSchema/key they need.
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns((string?)null);

        _txRunner = Substitute.For<IRecordStoreTransactionRunner>();
        _txRunner
            .ExecuteInTransactionAsync(Arg.Any<Func<IDbTransactionContext, Task>>())
            .Returns(ci => ci.Arg<Func<IDbTransactionContext, Task>>()(Substitute.For<IDbTransactionContext>()));

        _schemaManager = Substitute.For<IRecordStoreSchemaManager>();

        _embedding = Substitute.For<IEmbeddingService>();
        _embedding.Dimension.Returns(768);
        _embedding.ModelId.Returns("nomic-embed-text");

        _registry = new SchemaRegistry(
            new SchemaRegistryRepository(_sql),
            NullLogger<SchemaRegistry>.Instance);
        _actingUserAccessor = new ActingUserAccessor
            { ActingUser = ActingUserFixtures.Principal("test-user", "test-bypass") };
        _outboxPublisher = new OutboxPublisher(
            _events,
            new OutboxWriter(
                ReconciliationSchema.TableName,
                _sql,
                _txRunner),
            NullLogger<OutboxPublisher>.Instance);
        _relationResolver = new EntityRelationResolver(_registry, _entities, _authEvaluator);
        _schemaRegistration = new SchemaRegistrationOrchestrator(
            _schemaManager, _embedding, _registry);
        _auditLog = new AuditLog(_auditLogger);
        _sut = new ObjectMappingGrpcService(
            _entities,
            _txRunner,
            _outboxPublisher,
            _registry,
            new RelationValidator(),
            new EntityKeyAccessor(),
            new OutboxWriter(ReconciliationSchema.TableName, _sql, _txRunner),
            NullLogger<ObjectMappingGrpcService>.Instance,
            _actingUserAccessor,
            _authEvaluator,
            _relationResolver,
            _schemaRegistration,
            _auditLog);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static Struct MakePayload(Dictionary<string, Value> fields)
    {
        var s = new Struct();
        foreach (var (k, v) in fields) s.Fields[k] = v;
        return s;
    }

    private static SchemaDescriptor MakeSchema(string typeName) => new()
    {
        TypeName      = typeName,
        TableName     = typeName.ToLower() + "s",
        KeyColumn     = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns = [new ColumnDescriptor("Name", "text", true)],
        FkColumns     = [],
        VectorFields  = [],
        ChunkFields   = [],
        Relations     = [],
        TenantColumn  = "TenantId",
        Authorization = new Iverson.Api.Schema.AuthorizationRules(
            null,
            new List<Iverson.Api.Schema.RowPermission> { new("test-bypass", true, true, true) },
            new List<Iverson.Api.Schema.FieldPermission>())
    };

    private static Struct MakePayload(string keyColumnName, string keyValue)
    {
        var s = new Struct();
        s.Fields[keyColumnName] = Value.ForString(keyValue);
        return s;
    }

    private static TestServerCallContext MakeContext() => TestServerCallContext.Create();

    private static TypeDescriptor SimpleType(string name, params string[] extraScalars)
    {
        var td = new TypeDescriptor { TypeName = name };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
        foreach (var s in extraScalars)
            td.Properties.Add(new PropertyDescriptor { Name = s, ClrType = ClrType.ClrString });
        return td;
    }

    private EntityEvent? CaptureKafkaEvent(string topic)
    {
        EntityEvent? captured = null;
        _events
            .When(e => e.ProduceAsync(topic, Arg.Any<string>(), Arg.Any<EntityEvent>()))
            .Do(call => captured = call.ArgAt<EntityEvent>(2));
        return captured; // populated after sut call
    }

    /// <summary>
    /// Configures <see cref="_txRunner"/>'s <c>ExecuteInTransactionAsync</c> to actually invoke the
    /// captured transactional work against a fake <see cref="IDbTransactionContext"/>, recording
    /// every SQL statement issued inside it. Used by tests that need to assert on what happens
    /// inside the upsert/delete + outbox transaction (as opposed to tests that only care about
    /// the opportunistic publish, for which the default unconfigured no-op is sufficient).
    /// </summary>
    private List<string> CaptureTransactionalSql()
    {
        var executedSql = new List<string>();
        var fakeTx = Substitute.For<IDbTransactionContext>();
        fakeTx.ExecuteAsync(Arg.Do<string>(sql => executedSql.Add(sql)), Arg.Any<object?>()).Returns(0);

        _txRunner
            .ExecuteInTransactionAsync(Arg.Any<Func<IDbTransactionContext, Task>>())
            .Returns(call => call.Arg<Func<IDbTransactionContext, Task>>()(fakeTx));

        return executedSql;
    }

    // ── RegisterSchema ────────────────────────────────────────────────────────

    [Fact]
    public async Task RegisterSchema_WithNullRootType_ThrowsInvalidArgument()
    {
        var act = () => _sut.RegisterSchema(new SchemaRequest(), TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task RegisterSchema_ReturnsOrchestratorResult()
    {
        var mockOrchestrator = Substitute.For<ISchemaRegistrationOrchestrator>();
        mockOrchestrator.RegisterAsync(Arg.Any<SchemaRequest>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { "Widget" });
        var sut = new ObjectMappingGrpcService(
            _entities,
            _txRunner,
            _outboxPublisher,
            _registry,
            new RelationValidator(),
            new EntityKeyAccessor(),
            new OutboxWriter(ReconciliationSchema.TableName, _sql, _txRunner),
            NullLogger<ObjectMappingGrpcService>.Instance,
            _actingUserAccessor,
            _authEvaluator,
            _relationResolver,
            mockOrchestrator,
            _auditLog);

        var response = await sut
            .RegisterSchema(
                new SchemaRequest { RootType = SimpleType("Widget", "Name") },
                TestServerCallContext.Create(user: ActingUserFixtures.Principal("test-admin")));

        response.Success.Should().BeTrue();
        response.Registered.Should().BeEquivalentTo(new[] { "Widget" });
    }

    [Fact]
    public async Task RegisterSchema_Succeeds_LogsAdminOperation()
    {
        var mockOrchestrator = Substitute.For<ISchemaRegistrationOrchestrator>();
        mockOrchestrator.RegisterAsync(Arg.Any<SchemaRequest>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { "Widget" });
        var sut = new ObjectMappingGrpcService(
            _entities, _txRunner, _outboxPublisher, _registry,
            new RelationValidator(), new EntityKeyAccessor(),
            new OutboxWriter(ReconciliationSchema.TableName, _sql, _txRunner),
            NullLogger<ObjectMappingGrpcService>.Instance,
            _actingUserAccessor, _authEvaluator, _relationResolver, mockOrchestrator, _auditLog);

        await sut.RegisterSchema(
            new SchemaRequest { RootType = SimpleType("Widget", "Name") },
            TestServerCallContext.Create(user: ActingUserFixtures.Principal("test-admin")));

        _auditLogger.Received(1).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(v => v.ToString()!.Contains("RegisterSchema")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    // ── GetSchema ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSchema_ForUnrestrictedCaller_ReturnsAllTypesWithAllFields()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var response = await _sut.GetSchema(new GetSchemaRequest(), MakeContext());

        response.Types_.Select(t => t.Name).Should().BeEquivalentTo(new[] { "Author", "Article" });
        var author = response.Types_.Single(t => t.Name == "Author");
        author.Fields.Select(f => f.Name).Should().BeEquivalentTo(new[] { "Id", "Name", "Bio" });
        var article = response.Types_.Single(t => t.Name == "Article");
        article.Fields.Select(f => f.Name).Should().BeEquivalentTo(new[] { "Id", "Title", "Body", "AuthorId" });
    }

    [Fact]
    public async Task GetSchema_ProjectsEveryFieldLevelFlag_ThroughTheRpc()
    {
        // End-to-end cover for the members no other GetSchema test asserts: clr_type, is_array,
        // is_key, is_nullable, is_embedding, is_chunk and SchemaType.description. The flag
        // composition test alongside covers is_metadata / is_search_key / enrichment.
        var schema = SchemaFixtures.ArticleSchema() with
        {
            Description = "A published article.",
            ScalarColumns =
            [
                new ColumnDescriptor("Title", "text", false),
                new ColumnDescriptor("Body", "text", false),
                new ColumnDescriptor("AuthorId", "uuid", false),
                new ColumnDescriptor("Tags", "text[]", true)
            ],
            FieldDescriptions = new Dictionary<string, string> { ["Title"] = "The headline." }
        };
        await _registry.RegisterAsync(schema);

        var response = await _sut.GetSchema(new GetSchemaRequest(), MakeContext());

        var article = response.Types_.Single(t => t.Name == "Article");
        article.Description.Should().Be("A published article.");

        var key = article.Fields.Single(f => f.Name == "Id");
        key.ClrType.Should().Be(ClrType.ClrGuid);
        key.IsKey.Should().BeTrue();
        key.IsArray.Should().BeFalse();
        key.IsNullable.Should().BeFalse();

        // Scalar, non-key, carries a description, is the declared vector (embedding) field.
        var title = article.Fields.Single(f => f.Name == "Title");
        title.ClrType.Should().Be(ClrType.ClrString);
        title.Description.Should().Be("The headline.");
        title.IsKey.Should().BeFalse();
        title.IsArray.Should().BeFalse();
        title.IsEmbedding.Should().BeTrue();
        title.IsChunk.Should().BeFalse();

        // The declared chunk field.
        var body = article.Fields.Single(f => f.Name == "Body");
        body.IsChunk.Should().BeTrue();
        body.IsEmbedding.Should().BeFalse();

        // An array column: is_array is true and clr_type reports the *element* type.
        var tags = article.Fields.Single(f => f.Name == "Tags");
        tags.ClrType.Should().Be(ClrType.ClrString);
        tags.IsArray.Should().BeTrue();
        tags.IsNullable.Should().BeTrue();
    }

    [Fact]
    public async Task GetSchema_WithRestrictedFkFieldPermission_DropsBothTheFieldAndTheRelation()
    {
        // Regression: relations were filtered only on whether the related type survived, so a
        // FieldPermission excluding the FK column still disclosed its exact name as
        // relation.foreign_key — the very name field-level authorization had just removed.
        var article = SchemaFixtures.ArticleSchema() with
        {
            Authorization = new Iverson.Api.Schema.AuthorizationRules(
                null,
                new List<Iverson.Api.Schema.RowPermission> { new("test-bypass", true, true, true) },
                new List<Iverson.Api.Schema.FieldPermission>
                {
                    new("AuthorId", new List<string> { "editor" }, new List<string>())
                })
        };
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        await _registry.RegisterAsync(article);

        var response = await _sut.GetSchema(new GetSchemaRequest(), MakeContext());

        var projected = response.Types_.Single(t => t.Name == "Article");
        projected.Fields.Select(f => f.Name).Should().NotContain("AuthorId");
        projected.Relations.Select(r => r.ForeignKey).Should().NotContain("AuthorId");
        projected.Relations.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSchema_WithOneToManyRelation_UnderActiveFieldPermission_KeepsTheRelation()
    {
        // Regression (N1): a OneToMany's ForeignKey is a column on the RELATED type's table
        // (EntityRelationResolver.ResolveOneToManyAsync passes it to FetchByColumnAsync against
        // ToTableSchema(relatedSchema)), matched against this type's key. AllowedFields only ever
        // holds the DECLARING schema's own members, so testing the FK against it can never succeed
        // and would drop every OneToMany from the catalog whenever a FieldPermission is active.
        var article = SchemaFixtures.ArticleWithOneToManySchema() with
        {
            Authorization = new Iverson.Api.Schema.AuthorizationRules(
                null,
                new List<Iverson.Api.Schema.RowPermission> { new("test-bypass", true, true, true) },
                // Any active FieldPermission makes AllowedFields non-null — that is the trigger.
                new List<Iverson.Api.Schema.FieldPermission>
                {
                    new("Title", new List<string> { "editor" }, new List<string>())
                })
        };
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        await _registry.RegisterAsync(SchemaFixtures.UserArticleSchema());
        await _registry.RegisterAsync(article);

        var response = await _sut.GetSchema(new GetSchemaRequest(), MakeContext());

        var projected = response.Types_.Single(t => t.Name == "Article");
        // Sanity: AllowedFields really is active, i.e. this test is exercising the gated path.
        projected.Fields.Select(f => f.Name).Should().NotContain("Title");

        var oneToMany = projected.Relations
            .Should().ContainSingle(r => r.Kind == Iverson.Client.Contracts.RelationKind.OneToMany).Subject;
        oneToMany.PropertyName.Should().Be("UserArticles");
        oneToMany.RelatedType.Should().Be("UserArticle");
        oneToMany.ForeignKey.Should().Be("ArticleId");
    }

    [Fact]
    public async Task GetSchema_WithUnmappedKeyColumnSqlType_FailsLoudly()
    {
        // N2: a non-key column with an unmapped legacy SQL type is skipped, but the key is not
        // optional — a type with no is_key field is one the caller cannot issue a Get against,
        // and pass one's empty-field guard assumes the key survives.
        var schema = SchemaFixtures.AuthorSchema() with
        {
            KeyColumn = new ColumnDescriptor("Id", "money", false)
        };
        await _registry.RegisterAsync(schema);

        var act = () => _sut.GetSchema(new GetSchemaRequest(), MakeContext());

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task GetSchema_WithReadableFk_KeepsTheRelation()
    {
        // Companion to the test above: with no FieldPermission on the FK, both the surviving
        // related type and the relation itself must still be reported.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var response = await _sut.GetSchema(new GetSchemaRequest(), MakeContext());

        var article = response.Types_.Single(t => t.Name == "Article");
        var relation = article.Relations.Should().ContainSingle().Subject;
        relation.PropertyName.Should().Be("Author");
        relation.RelatedType.Should().Be("Author");
        relation.ForeignKey.Should().Be("AuthorId");
        relation.Kind.Should().Be(Iverson.Client.Contracts.RelationKind.ManyToOne);
    }

    [Fact]
    public async Task GetSchema_WithDifferentlyCasedRelatedTypeName_KeepsTheRelation()
    {
        // SchemaRegistry keys OrdinalIgnoreCase and EntityRelationResolver resolves through it,
        // so a relation the query path would honour must not be dropped from the catalog.
        var article = SchemaFixtures.ArticleSchema() with
        {
            Relations = [new Iverson.Api.Schema.RelationDescriptor(
                "Author", Iverson.Api.Schema.RelationKind.ManyToOne, "author", "AuthorId")]
        };
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        await _registry.RegisterAsync(article);

        var response = await _sut.GetSchema(new GetSchemaRequest(), MakeContext());

        response.Types_.Single(t => t.Name == "Article").Relations.Should().ContainSingle();
    }

    [Fact]
    public async Task GetSchema_WithUnmappedLegacySqlType_SkipsTheColumnAndKeepsTheCatalog()
    {
        // SchemaRegistry rehydrates descriptors persisted by older builds, so a column may carry
        // a SQL type this build no longer maps. That must cost one column, not the whole RPC.
        var schema = SchemaFixtures.AuthorSchema() with
        {
            ScalarColumns =
            [
                new ColumnDescriptor("Name", "text", false),
                new ColumnDescriptor("Legacy", "money", true)
            ]
        };
        await _registry.RegisterAsync(schema);

        var response = await _sut.GetSchema(new GetSchemaRequest(), MakeContext());

        var author = response.Types_.Single(t => t.Name == "Author");
        author.Fields.Select(f => f.Name).Should().BeEquivalentTo(new[] { "Id", "Name" });
    }

    [Fact]
    public async Task GetSchema_WhenEveryFieldIsFilteredOut_OmitsTheTypeEntirely()
    {
        // Spec §4 server test 4. The production RowFieldAuthorizationEvaluator unconditionally
        // re-admits the key column so it can never produce an empty AllowedFields — this pins the
        // fail-closed guard against a substituted evaluator that does.
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var evaluator = Substitute.For<IRowFieldAuthorizationEvaluator>();
        evaluator.Evaluate(Arg.Any<SchemaDescriptor>(), Arg.Any<ClaimsPrincipal?>(), Arg.Any<AuthorizationAction>())
            .Returns(new AuthorizationDecision(
                false, false, null, null, new HashSet<string>(), "TenantId", "test-tenant"));

        var sut = new ObjectMappingGrpcService(
            _entities, _txRunner, _outboxPublisher, _registry,
            new RelationValidator(), new EntityKeyAccessor(),
            new OutboxWriter(ReconciliationSchema.TableName, _sql, _txRunner),
            NullLogger<ObjectMappingGrpcService>.Instance,
            _actingUserAccessor, evaluator, _relationResolver, _schemaRegistration, _auditLog);

        var response = await sut.GetSchema(new GetSchemaRequest(), MakeContext());

        response.Types_.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSchema_WithDeniedType_OmitsItEntirely()
    {
        // No RowPermission matches "test-bypass" and there's no OwnerField — the evaluator
        // denies this type outright, so its name must never appear in the response.
        var denied = SchemaFixtures.AuthorSchema() with
        {
            Authorization = new Iverson.Api.Schema.AuthorizationRules(
                null,
                new List<Iverson.Api.Schema.RowPermission> { new("some-other-role", true, true, true) },
                new List<Iverson.Api.Schema.FieldPermission>())
        };
        await _registry.RegisterAsync(denied);

        var response = await _sut.GetSchema(new GetSchemaRequest(), MakeContext());

        response.Types_.Select(t => t.Name).Should().NotContain("Author");
    }

    [Fact]
    public async Task GetSchema_WithRestrictedFieldPermission_OmitsFieldAndItsDescription()
    {
        var schema = SchemaFixtures.AuthorSchema() with
        {
            FieldDescriptions = new Dictionary<string, string> { ["Bio"] = "A short biography." },
            Authorization = new Iverson.Api.Schema.AuthorizationRules(
                null,
                new List<Iverson.Api.Schema.RowPermission> { new("test-bypass", true, true, true) },
                new List<Iverson.Api.Schema.FieldPermission>
                {
                    new("Bio", new List<string> { "premium" }, new List<string>())
                })
        };
        await _registry.RegisterAsync(schema);

        var response = await _sut.GetSchema(new GetSchemaRequest(), MakeContext());

        var author = response.Types_.Single(t => t.Name == "Author");
        author.Fields.Select(f => f.Name).Should().NotContain("Bio");

        // Asserted separately from the field-list check above: the excluded field's
        // description leaking anywhere in the response would itself be a disclosure.
        author.Fields.Select(f => f.Description).Should().NotContain("A short biography.");
    }

    [Fact]
    public async Task GetSchema_WithRelationToOmittedType_DropsTheRelation()
    {
        // Article relates to Author; only Article is registered, so the evaluator never even
        // considers Author — it simply doesn't survive pass one, and the relation pointing at
        // it must be dropped in pass two.
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var response = await _sut.GetSchema(new GetSchemaRequest(), MakeContext());

        var article = response.Types_.Single(t => t.Name == "Article");
        article.Relations.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSchema_ComposesMultipleFlagsAndEnrichmentKinds_OnASingleField()
    {
        var schema = SchemaFixtures.AuthorSchema() with
        {
            MetadataColumns  = ["Name"],
            SearchKeyColumns = ["Name"],
            EnrichmentTargets =
            [
                new EnrichmentTarget("Name", EnrichmentKind.Summary, null),
                new EnrichmentTarget("Name", EnrichmentKind.Keywords, null)
            ]
        };
        await _registry.RegisterAsync(schema);

        var response = await _sut.GetSchema(new GetSchemaRequest(), MakeContext());

        var nameField = response.Types_.Single(t => t.Name == "Author").Fields.Single(f => f.Name == "Name");
        nameField.IsMetadata.Should().BeTrue();
        nameField.IsSearchKey.Should().BeTrue();
        nameField.SearchKeyOrder.Should().Be(0);
        nameField.Enrichment.Should().BeEquivalentTo(
            new[] { SchemaEnrichmentKind.EnrichmentSummary, SchemaEnrichmentKind.EnrichmentKeywords });
    }

    // ── Post ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_WithMissingKey_GeneratesValidGuid()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _sql.QuerySingleOrDefaultAsync<string>(Arg.Any<string>(), Arg.Any<object?>())
            .Returns(AuthorJson);

        EntityEvent? evt = null;
        _events.When(e => e.ProduceAsync(EntityTopics.Events, Arg.Any<string>(), Arg.Any<EntityEvent>()))
               .Do(call => evt = call.ArgAt<EntityEvent>(2));

        var payload = MakePayload(new() { ["Name"] = Value.ForString("Alice") });
        var response = await _sut.Post(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        response.Success.Should().BeTrue();
        Guid.TryParse(evt!.Key, out var g).Should().BeTrue();
        g.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task Post_WithClientProvidedKey_ThrowsInvalidArgument()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var payload = MakePayload(new()
        {
            ["Id"]   = Value.ForString(AuthorId),
            ["Name"] = Value.ForString("Alice")
        });
        var act = () => _sut.Post(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("server-generated");
    }

    [Fact]
    public async Task Post_ExecutesUpsertSql_DirectlyToPostgres()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        var executedSql = CaptureTransactionalSql();

        var payload = MakePayload(new()
        {
            ["Name"] = Value.ForString("Alice")
        });
        await _sut.Post(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        executedSql.Should().Contain(s => s.Contains("json_populate_record"));
    }

    [Fact]
    public async Task Post_InsertsReconciliationQueueRowInSameTransactionAsUpsert()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var capturedWork = default(Func<IDbTransactionContext, Task>);
        _txRunner.ExecuteInTransactionAsync(Arg.Do<Func<IDbTransactionContext, Task>>(w => capturedWork = w))
            .Returns(Task.CompletedTask);

        var payload = MakePayload(new()
        {
            ["Title"]    = Value.ForString("Test"),
            ["AuthorId"] = Value.ForString(AuthorId)
        });
        await _sut.Post(
            new MappingWriteRequest { TypeName = "Article", Payload = payload },
            TestServerCallContext.Create());

        capturedWork.Should().NotBeNull();

        // Execute the captured transactional work against a fake transaction context and
        // assert it issues BOTH an upsert into the entity table AND an insert into the
        // reconciliation-queue table — proving both happen inside the one transaction
        // this test captured, not as two independent top-level calls.
        var executedSql = new List<string>();
        var fakeTx = Substitute.For<IDbTransactionContext>();
        fakeTx.ExecuteAsync(Arg.Do<string>(sql => executedSql.Add(sql)), Arg.Any<object?>()).Returns(0);

        await capturedWork!(fakeTx);

        executedSql.Should().Contain(sql => sql.Contains("INSERT INTO \"articles\""));
        executedSql.Should().Contain(sql => sql.Contains($"INSERT INTO \"{ReconciliationSchema.TableName}\""));
    }

    [Fact]
    public async Task Post_EmitsCreatedEvent_WithCorrectTypeName()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _sql.QuerySingleOrDefaultAsync<string>(Arg.Any<string>(), Arg.Any<object?>())
            .Returns(AuthorJson);

        EntityEvent? evt = null;
        _events.When(e => e.ProduceAsync(EntityTopics.Events, Arg.Any<string>(), Arg.Any<EntityEvent>()))
               .Do(call => evt = call.ArgAt<EntityEvent>(2));

        var payload = MakePayload(new()
        {
            ["Name"] = Value.ForString("Alice")
        });
        await _sut.Post(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        evt.Should().NotBeNull();
        evt!.TypeName.Should().Be("Author");
        Guid.TryParse(evt.Key, out _).Should().BeTrue();
        evt.EventType.Should().Be(EntityEventType.Created);
    }

    [Fact]
    public async Task Post_WhenSchemaNotRegistered_ThrowsFailedPrecondition()
    {
        var payload = MakePayload(new() { ["Name"] = Value.ForString("Alice") });
        var act = () => _sut.Post(
            new MappingWriteRequest { TypeName = "Ghost", Payload = payload },
            TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
    }

    [Fact]
    public async Task Post_ReturnsPayloadAsData_NotDbRefetch()
    {
        var schema = MakeSchema("Player");
        await _registry.RegisterAsync(schema);
        var payload = MakePayload(new Dictionary<string, Value>());
        var request = new MappingWriteRequest { TypeName = "Player", Payload = payload, TraceId = "t1" };

        var response = await _sut.Post(request, MakeContext());

        response.Success.Should().BeTrue();
        response.Data.Should().BeSameAs(request.Payload);
        response.TraceId.Should().Be("t1");
        _ = _sql.DidNotReceive().QuerySingleOrDefaultAsync<string>(
            Arg.Any<string>(), Arg.Any<object>());
    }

    [Fact]
    public async Task Post_WithInvalidFkGuid_ThrowsInvalidArgument()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
        _sql.QuerySingleOrDefaultAsync<string>(Arg.Any<string>(), Arg.Any<object?>())
            .Returns(ArticleJson);

        var payload = MakePayload(new()
        {
            ["Title"]    = Value.ForString("Hello"),
            ["AuthorId"] = Value.ForString("not-a-guid")
        });
        var act = () => _sut.Post(
            new MappingWriteRequest { TypeName = "Article", Payload = payload },
            TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    // ── Post authorization ───────────────────────────────────────────────────

    [Fact]
    public async Task Post_WithNoAuthorizationRulesConfigured_ThrowsPermissionDenied()
    {
        var schema = SchemaFixtures.AuthorSchema() with { Authorization = null };
        await _registry.RegisterAsync(schema);

        var payload = MakePayload(new() { ["Name"] = Value.ForString("Alice") });
        var act = () => _sut.Post(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task Post_WithNoActingUser_ThrowsPermissionDenied()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _actingUserAccessor.ActingUser = null;

        var payload = MakePayload(new() { ["Name"] = Value.ForString("Alice") });
        var act = () => _sut.Post(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task Post_WithSuppliedKey_AndUnauthorizedCaller_ThrowsPermissionDenied_NotInvalidArgument()
    {
        var schema = SchemaFixtures.AuthorSchema() with { Authorization = null };
        await _registry.RegisterAsync(schema);
        var payload = MakePayload(new()
        {
            ["Id"]   = Value.ForString(Guid.NewGuid().ToString()),
            ["Name"] = Value.ForString("Alice")
        });

        var act = () => _sut.Post(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("someone-else")]
    public async Task Post_ForOrdinaryCaller_ForceSetsOwnerFieldToActingUserSub(string? clientSuppliedOwnerId)
    {
        await _registry.RegisterAsync(OwnedAuthorSchema(withBypassRole: false));

        var fields = new Dictionary<string, Value>
        {
            ["Name"] = Value.ForString("Alice")
        };
        if (clientSuppliedOwnerId is not null)
            fields["OwnerId"] = Value.ForString(clientSuppliedOwnerId);
        var payload = MakePayload(fields);

        var response = await _sut.Post(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        response.Success.Should().BeTrue();
        response.Data.Fields["OwnerId"].StringValue.Should().Be("test-user");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("someone-else")]
    public async Task Post_WithBypassRole_LeavesOwnerFieldUntouched(string? clientSuppliedOwnerId)
    {
        await _registry.RegisterAsync(OwnedAuthorSchema(withBypassRole: true));

        var fields = new Dictionary<string, Value>
        {
            ["Name"] = Value.ForString("Alice")
        };
        if (clientSuppliedOwnerId is not null)
            fields["OwnerId"] = Value.ForString(clientSuppliedOwnerId);
        var payload = MakePayload(fields);

        var response = await _sut.Post(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        response.Success.Should().BeTrue();
        if (clientSuppliedOwnerId is null)
            response.Data.Fields.Should().NotContainKey("OwnerId");
        else
            response.Data.Fields["OwnerId"].StringValue.Should().Be(clientSuppliedOwnerId);
    }

    [Fact]
    public async Task Post_ForOrdinaryCaller_StampsTenantOntoPayload()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema(withBypassRole: false));

        var payload = MakePayload(new()
        {
            ["Name"] = Value.ForString("Alice")
        });

        var response = await _sut.Post(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        response.Success.Should().BeTrue();
        response.Data.Fields["TenantId"].StringValue.Should().Be("test-tenant");
    }

    [Fact]
    public async Task Post_WithBypassRole_StillStampsTenantOntoPayload()
    {
        // Tenant is strictly additive: a CanWriteAll bypass role must not exempt the caller
        // from the tenant boundary (unlike OwnerId, which is intentionally left untouched for
        // bypass callers).
        await _registry.RegisterAsync(OwnedAuthorSchema(withBypassRole: true));

        var payload = MakePayload(new()
        {
            ["Name"] = Value.ForString("Alice")
        });

        var response = await _sut.Post(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        response.Success.Should().BeTrue();
        response.Data.Fields["TenantId"].StringValue.Should().Be("test-tenant");
    }

    [Fact]
    public async Task Post_WithRestrictedFieldInWritePayload_ThrowsInvalidArgument()
    {
        var schema = SchemaFixtures.AuthorSchema() with
        {
            Authorization = new Iverson.Api.Schema.AuthorizationRules(
                null,
                new List<Iverson.Api.Schema.RowPermission> { new("test-bypass", true, true, true) },
                new List<Iverson.Api.Schema.FieldPermission>
                {
                    new("Bio", new List<string>(), new List<string> { "premium" })
                })
        };
        await _registry.RegisterAsync(schema);

        var payload = MakePayload(new()
        {
            ["Name"] = Value.ForString("Alice"),
            ["Bio"]  = Value.ForString("Writer")
        });
        var act = () => _sut.Post(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task Post_ForOrdinaryCaller_WithFieldPermissionRestrictingOwnerColumn_StillForceSetsOwnerField()
    {
        var schema = OwnedAuthorSchema(withBypassRole: false) with
        {
            Authorization = new Iverson.Api.Schema.AuthorizationRules(
                "OwnerId",
                new List<Iverson.Api.Schema.RowPermission>(),
                new List<Iverson.Api.Schema.FieldPermission>
                {
                    new("OwnerId", new List<string>(), new List<string> { "premium" })
                })
        };
        await _registry.RegisterAsync(schema);

        var payload = MakePayload(new()
        {
            ["Name"] = Value.ForString("Alice")
        });

        var response = await _sut.Post(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        response.Success.Should().BeTrue();
        response.Data.Fields["OwnerId"].StringValue.Should().Be("test-user");
    }

    // ── Get ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_WhenEntityExists_ReturnsSuccessWithParsedData()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(AuthorJson);

        var response = await _sut.Get(
            new MappingGetRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        response.Success.Should().BeTrue();
        response.Data.Fields["Name"].StringValue.Should().Be("Alice");
    }

    [Fact]
    public async Task Get_WhenEntityNotFound_ReturnsFailureResponse()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns((string?)null);

        var response = await _sut.Get(
            new MappingGetRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        response.Success.Should().BeFalse();
        response.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task Get_WhenSchemaNotRegistered_ThrowsFailedPrecondition()
    {
        var act = () => _sut.Get(
            new MappingGetRequest { TypeName = "Ghost", Key = AuthorId },
            TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
    }

    [Fact]
    public async Task Get_WithDepthGreaterThanZero_CallsRelationResolver()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(ArticleJson);

        var mockResolver = Substitute.For<IEntityRelationResolver>();
        var sut = new ObjectMappingGrpcService(
            _entities,
            _txRunner,
            _outboxPublisher,
            _registry,
            new RelationValidator(),
            new EntityKeyAccessor(),
            new OutboxWriter(ReconciliationSchema.TableName, _sql, _txRunner),
            NullLogger<ObjectMappingGrpcService>.Instance,
            _actingUserAccessor,
            _authEvaluator,
            mockResolver,
            _schemaRegistration,
            _auditLog);

        await sut.Get(new MappingGetRequest { TypeName = "Article", Key = ArticleId, Depth = 1 }, TestServerCallContext.Create());

        await mockResolver.Received(1)
            .ResolveRelationsAsync(
                Arg.Any<Struct>(),
                Arg.Any<SchemaDescriptor>(),
                1,
                Arg.Any<ClaimsPrincipal?>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Get_WithDepthZero_DoesNotCallRelationResolver()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>()).Returns(ArticleJson);

        var mockResolver = Substitute.For<IEntityRelationResolver>();
        var sut = new ObjectMappingGrpcService(
            _entities,
            _txRunner,
            _outboxPublisher,
            _registry,
            new RelationValidator(),
            new EntityKeyAccessor(),
            new OutboxWriter(ReconciliationSchema.TableName, _sql, _txRunner),
            NullLogger<ObjectMappingGrpcService>.Instance,
            _actingUserAccessor,
            _authEvaluator,
            mockResolver,
            _schemaRegistration,
            _auditLog);

        await sut.Get(new MappingGetRequest { TypeName = "Article", Key = ArticleId, Depth = 0 }, TestServerCallContext.Create());

        await mockResolver.DidNotReceiveWithAnyArgs().ResolveRelationsAsync(
            default!, default!, default, default, default);
    }

    // ── Get authorization ────────────────────────────────────────────────────

    private static SchemaDescriptor OwnedAuthorSchema(bool withBypassRole = false) => new()
    {
        TypeName       = "Author",
        TableName      = "authors",
        CollectionName = null,
        KeyColumn      = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns  =
        [
            new ColumnDescriptor("Name", "text", false),
            new ColumnDescriptor("OwnerId", "text", false),
            new ColumnDescriptor("TenantId", "text", false)
        ],
        FkColumns     = [],
        VectorFields  = [],
        ChunkFields   = [],
        Relations     = [],
        TenantColumn  = "TenantId",
        Authorization = new Iverson.Api.Schema.AuthorizationRules(
            "OwnerId",
            withBypassRole
                ? new List<Iverson.Api.Schema.RowPermission> { new("test-bypass", true, true, true) }
                : new List<Iverson.Api.Schema.RowPermission>(),
            new List<Iverson.Api.Schema.FieldPermission>())
    };

    private static SchemaDescriptor OwnedTagSchema() => new()
    {
        TypeName       = "Tag",
        TableName      = "tags",
        CollectionName = null,
        KeyColumn      = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns  =
        [
            new ColumnDescriptor("Label", "text", false),
            new ColumnDescriptor("OwnerId", "text", false)
        ],
        FkColumns     = [],
        VectorFields  = [],
        ChunkFields   = [],
        Relations     = [],
        TenantColumn  = "TenantId",
        Authorization = new Iverson.Api.Schema.AuthorizationRules(
            "OwnerId",
            new List<Iverson.Api.Schema.RowPermission>(),
            new List<Iverson.Api.Schema.FieldPermission>())
    };

    [Fact]
    public async Task Get_WithNoAuthorizationRulesConfigured_ReturnsNotFound()
    {
        var schema = SchemaFixtures.AuthorSchema() with { Authorization = null };
        await _registry.RegisterAsync(schema);
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(AuthorJson);

        var response = await _sut.Get(
            new MappingGetRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        response.Success.Should().BeFalse();
        response.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task Get_WithNoActingUser_ReturnsNotFound()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(AuthorJson);
        _actingUserAccessor.ActingUser = null;

        var response = await _sut.Get(
            new MappingGetRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        response.Success.Should().BeFalse();
        response.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task Get_WithMatchingOwner_ReturnsSuccess()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema());
        var ownedJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","OwnerId":"test-user","TenantId":"test-tenant"}""";
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(ownedJson);

        var response = await _sut.Get(
            new MappingGetRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        response.Success.Should().BeTrue();
        response.Data.Fields["Name"].StringValue.Should().Be("Alice");
    }

    [Fact]
    public async Task Get_WithBypassRole_ReturnsSuccess_EvenWhenNotOwner()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema(withBypassRole: true));
        var ownedJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","OwnerId":"someone-else","TenantId":"test-tenant"}""";
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(ownedJson);

        var response = await _sut.Get(
            new MappingGetRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        response.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Get_WithNonMatchingOwner_ReturnsNotFound()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema());
        var ownedJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","OwnerId":"someone-else","TenantId":"test-tenant"}""";
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(ownedJson);

        var response = await _sut.Get(
            new MappingGetRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        response.Success.Should().BeFalse();
        response.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task Get_WithNonMatchingTenant_ReturnsNotFound()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        var crossTenantJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","Bio":"Writer","TenantId":"other-tenant"}""";
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(crossTenantJson);

        var response = await _sut.Get(
            new MappingGetRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        response.Success.Should().BeFalse();
        response.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task Get_WithBypassRoleAndNonMatchingTenant_ReturnsNotFound()
    {
        // Tenant is strictly additive: a CanReadAll bypass role must not exempt the caller
        // from the tenant boundary.
        await _registry.RegisterAsync(OwnedAuthorSchema(withBypassRole: true));
        var crossTenantJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","OwnerId":"someone-else","TenantId":"other-tenant"}""";
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(crossTenantJson);

        var response = await _sut.Get(
            new MappingGetRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        response.Success.Should().BeFalse();
        response.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task Get_WithRestrictedField_OmitsFieldFromResponse()
    {
        var schema = SchemaFixtures.AuthorSchema() with
        {
            Authorization = new Iverson.Api.Schema.AuthorizationRules(
                null,
                new List<Iverson.Api.Schema.RowPermission> { new("test-bypass", true, true, true) },
                new List<Iverson.Api.Schema.FieldPermission>
                {
                    new("Bio", new List<string> { "premium" }, new List<string>())
                })
        };
        await _registry.RegisterAsync(schema);
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(AuthorJson);

        var response = await _sut.Get(
            new MappingGetRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        response.Success.Should().BeTrue();
        response.Data.Fields.Should().ContainKey("Name");
        response.Data.Fields.Should().NotContainKey("Bio");
    }

    [Fact]
    public async Task Get_WithRelatedEntities_OmitsDeniedRelatedEntity_KeepsAllowedOne()
    {
        var postId = "33333333-0000-0000-0000-000000000003";
        var allowedTagId = "44444444-0000-0000-0000-000000000004";
        var deniedTagId  = "44444444-0000-0000-0000-000000000005";

        await _registry.RegisterAsync(SchemaFixtures.PostWithTagsSchema());
        await _registry.RegisterAsync(OwnedTagSchema());

        var postJson = $$"""{"Id":"{{postId}}","Title":"Hello","TagIds":["{{allowedTagId}}","{{deniedTagId}}"],"TenantId":"test-tenant"}""";
        _entities
            .FetchByKeyAsync(
                Arg.Is<TableSchema>(s => s.TableName == "posts"),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(postJson);

        var allowedTagJson = $$"""{"Id":"{{allowedTagId}}","Label":"dotnet","OwnerId":"test-user","TenantId":"test-tenant"}""";
        var deniedTagJson  = $$"""{"Id":"{{deniedTagId}}","Label":"csharp","OwnerId":"someone-else","TenantId":"test-tenant"}""";
        _entities
            .FetchManyByKeysAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<bool>(), 
                Arg.Any<string?>())
            .Returns(new[] { new KeyedRow(allowedTagId, allowedTagJson), new KeyedRow(deniedTagId, deniedTagJson) });

        var response = await _sut.Get(
            new MappingGetRequest { TypeName = "Post", Key = postId, Depth = 1 },
            MakeContext());

        response.Success.Should().BeTrue();
        var tags = response.Data.Fields["Tags"].ListValue.Values;
        tags.Should().ContainSingle();
        tags[0].StructValue.Fields["Label"].StringValue.Should().Be("dotnet");
    }

    [Fact]
    public async Task Get_WithRelatedEntities_OmitsCrossTenantRelatedEntity_KeepsSameTenantOne()
    {
        // Relation traversal (depth>0) must not surface a related row belonging to another
        // tenant, even though PostWithTagsSchema/TagSchema grant "test-bypass" full row access
        // (tenant is strictly additive — it must win over row-level bypass here too).
        var postId          = "33333333-0000-0000-0000-000000000006";
        var sameTenantTagId  = "44444444-0000-0000-0000-000000000006";
        var crossTenantTagId = "44444444-0000-0000-0000-000000000007";

        await _registry.RegisterAsync(SchemaFixtures.PostWithTagsSchema());
        await _registry.RegisterAsync(SchemaFixtures.TagSchema());

        var postJson = $$"""{"Id":"{{postId}}","Title":"Hello","TagIds":["{{sameTenantTagId}}","{{crossTenantTagId}}"],"TenantId":"test-tenant"}""";
        _entities
            .FetchByKeyAsync(
                Arg.Is<TableSchema>(s => s.TableName == "posts"),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(postJson);

        var sameTenantTagJson  = $$"""{"Id":"{{sameTenantTagId}}","Label":"dotnet","TenantId":"test-tenant"}""";
        var crossTenantTagJson = $$"""{"Id":"{{crossTenantTagId}}","Label":"csharp","TenantId":"other-tenant"}""";
        _entities
            .FetchManyByKeysAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(new[] { new KeyedRow(sameTenantTagId, sameTenantTagJson), new KeyedRow(crossTenantTagId, crossTenantTagJson) });

        var response = await _sut.Get(
            new MappingGetRequest { TypeName = "Post", Key = postId, Depth = 1 },
            MakeContext());

        response.Success.Should().BeTrue();
        var tags = response.Data.Fields["Tags"].ListValue.Values;
        tags.Should().ContainSingle();
        tags[0].StructValue.Fields["Label"].StringValue.Should().Be("dotnet");
    }

    [Fact]
    public async Task Get_WithFieldPermissionExcludingFkColumn_OmitsRelatedEntity()
    {
        // Regression test: AllowedFields normally always includes every FK column (so recursive
        // relation resolution keeps working by default), but nothing stops a FieldPermission from
        // explicitly excluding an FK column by name. When that happens, the FK column gets masked
        // out of the entity struct before relation resolution runs, so the relation must be
        // correctly omitted — not throw, not return partial/broken data.
        var schema = SchemaFixtures.ArticleSchema() with
        {
            Authorization = new Iverson.Api.Schema.AuthorizationRules(
                null,
                new List<Iverson.Api.Schema.RowPermission> { new("test-bypass", true, true, true) },
                new List<Iverson.Api.Schema.FieldPermission>
                {
                    new("AuthorId", new List<string> { "premium" }, new List<string>())
                })
        };
        await _registry.RegisterAsync(schema);
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        _entities
            .FetchByKeyAsync(
                Arg.Is<TableSchema>(s => s.TableName == "articles"),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(ArticleJson);
        _entities
            .FetchByKeyAsync(
                Arg.Is<TableSchema>(s => s.TableName == "authors"),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(AuthorJson);

        var response = await _sut.Get(
            new MappingGetRequest { TypeName = "Article", Key = ArticleId, Depth = 1 },
            TestServerCallContext.Create());

        response.Success.Should().BeTrue();
        response.Data.Fields.Should().ContainKey("Title");
        response.Data.Fields.Should().NotContainKey("AuthorId");
        response.Data.Fields.Should().NotContainKey("Author");
    }

    // ── Get audit logging ────────────────────────────────────────────────────

    private void AssertAuditLogged(string expectedReasonSubstring) =>
        _auditLogger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(v => v.ToString()!.Contains(expectedReasonSubstring)),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());

    [Fact]
    public async Task Get_AccessDenied_LogsAuditDeniedWithAccessDenied()
    {
        var schema = SchemaFixtures.AuthorSchema() with { Authorization = null };
        await _registry.RegisterAsync(schema);
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(AuthorJson);

        await _sut.Get(
            new MappingGetRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        AssertAuditLogged("AccessDenied");
    }

    [Fact]
    public async Task Get_OwnerMismatch_LogsAuditDeniedWithOwnerMismatch()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema());
        var ownedJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","OwnerId":"someone-else","TenantId":"test-tenant"}""";
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(ownedJson);

        await _sut.Get(
            new MappingGetRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        AssertAuditLogged("OwnerMismatch");
    }

    [Fact]
    public async Task Get_TenantMismatch_LogsAuditDeniedWithTenantMismatch()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        var crossTenantJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","Bio":"Writer","TenantId":"other-tenant"}""";
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(crossTenantJson);

        await _sut.Get(
            new MappingGetRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        AssertAuditLogged("TenantMismatch");
    }

    // ── Update ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_WithValidKey_EmitsUpdatedEvent()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        EntityEvent? evt = null;
        _events
            .When(e => e.ProduceAsync(
                EntityTopics.Events,
                Arg.Any<string>(),
                Arg.Any<EntityEvent>()))
            .Do(call => evt = call.ArgAt<EntityEvent>(2));

        var payload = MakePayload(new()
        {
            ["Id"]   = Value.ForString(AuthorId),
            ["Name"] = Value.ForString("Alice Updated")
        });
        var response = await _sut.Update(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        response.Success.Should().BeTrue();
        evt!.Key.Should().Be(AuthorId);
        evt.EventType.Should().Be(EntityEventType.Updated);
    }

    [Fact]
    public async Task Update_ExecutesUpsertSql_DirectlyToPostgres()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        var executedSql = CaptureTransactionalSql();

        var payload = MakePayload(new()
        {
            ["Id"]   = Value.ForString(AuthorId),
            ["Name"] = Value.ForString("Alice Updated")
        });
        await _sut.Update(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        executedSql.Should().Contain(s => s.Contains("json_populate_record"));
    }

    [Fact]
    public async Task Update_InsertsReconciliationQueueRowInSameTransactionAsUpsert()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var capturedWork = default(Func<IDbTransactionContext, Task>);
        _txRunner
            .ExecuteInTransactionAsync(
                Arg.Do<Func<IDbTransactionContext, Task>>(w => capturedWork = w))
            .Returns(Task.CompletedTask);

        var payload = MakePayload(new()
        {
            ["Id"]       = Value.ForString(ArticleId),
            ["Title"]    = Value.ForString("Updated Title"),
            ["AuthorId"] = Value.ForString(AuthorId)
        });
        await _sut.Update(
            new MappingWriteRequest { TypeName = "Article", Payload = payload },
            TestServerCallContext.Create());

        capturedWork.Should().NotBeNull();

        var executedSql = new List<string>();
        var fakeTx = Substitute.For<IDbTransactionContext>();
        fakeTx.ExecuteAsync(Arg.Do<string>(sql => executedSql.Add(sql)), Arg.Any<object?>()).Returns(0);

        await capturedWork!(fakeTx);

        executedSql.Should().Contain(sql => sql.Contains("INSERT INTO \"articles\""));
        executedSql.Should().Contain(sql => sql.Contains($"INSERT INTO \"{ReconciliationSchema.TableName}\""));
    }

    [Fact]
    public async Task Update_WithEmptyKey_ThrowsInvalidArgument()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        var payload = MakePayload(new() { ["Name"] = Value.ForString("Alice") });
        var act = () => _sut.Update(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("Update requires");
    }

    [Fact]
    public async Task Update_WhenSchemaNotRegistered_ThrowsFailedPrecondition()
    {
        var payload = MakePayload(new() { ["Id"] = Value.ForString(AuthorId) });
        var act = () => _sut.Update(
            new MappingWriteRequest { TypeName = "Ghost", Payload = payload },
            TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
    }

    // ── Update authorization ──────────────────────────────────────────────────

    [Fact]
    public async Task Update_WithNoAuthorizationRulesConfigured_ThrowsPermissionDenied()
    {
        var schema = SchemaFixtures.AuthorSchema() with { Authorization = null };
        await _registry.RegisterAsync(schema);

        var payload = MakePayload(new()
        {
            ["Id"]   = Value.ForString(AuthorId),
            ["Name"] = Value.ForString("Alice Updated")
        });
        var act = () => _sut.Update(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task Update_WithNoActingUser_ThrowsPermissionDenied()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _actingUserAccessor.ActingUser = null;

        var payload = MakePayload(new()
        {
            ["Id"]   = Value.ForString(AuthorId),
            ["Name"] = Value.ForString("Alice Updated")
        });
        var act = () => _sut.Update(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task Update_WithMatchingOwner_ReturnsSuccess()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema());
        var ownedJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","OwnerId":"test-user","TenantId":"test-tenant"}""";
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(ownedJson);

        var payload = MakePayload(new()
        {
            ["Id"]      = Value.ForString(AuthorId),
            ["Name"]    = Value.ForString("Alice Updated"),
            ["OwnerId"] = Value.ForString("test-user")
        });
        var response = await _sut.Update(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        response.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Update_WithBypassRole_ReturnsSuccess_EvenWhenNotOwner()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema(withBypassRole: true));
        var ownedJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","OwnerId":"someone-else","TenantId":"test-tenant"}""";
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(ownedJson);

        var payload = MakePayload(new()
        {
            ["Id"]      = Value.ForString(AuthorId),
            ["Name"]    = Value.ForString("Alice Updated"),
            ["OwnerId"] = Value.ForString("someone-else")
        });
        var response = await _sut.Update(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        response.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Update_WithNonMatchingOwner_ThrowsPermissionDenied()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema());
        var ownedJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","OwnerId":"someone-else","TenantId":"test-tenant"}""";
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(ownedJson);

        var payload = MakePayload(new()
        {
            ["Id"]      = Value.ForString(AuthorId),
            ["Name"]    = Value.ForString("Alice Updated"),
            ["OwnerId"] = Value.ForString("someone-else")
        });
        var act = () => _sut.Update(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task Update_WithNonMatchingTenant_ThrowsPermissionDenied()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema());
        var crossTenantJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","OwnerId":"test-user","TenantId":"other-tenant"}""";
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(crossTenantJson);

        var payload = MakePayload(new()
        {
            ["Id"]      = Value.ForString(AuthorId),
            ["Name"]    = Value.ForString("Alice Updated"),
            ["OwnerId"] = Value.ForString("test-user")
        });
        var act = () => _sut.Update(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task Update_WithBypassRoleAndNonMatchingTenant_ThrowsPermissionDenied()
    {
        // Tenant is strictly additive: a CanWriteAll bypass role must not exempt the caller
        // from the tenant boundary.
        await _registry.RegisterAsync(OwnedAuthorSchema(withBypassRole: true));
        var crossTenantJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","OwnerId":"someone-else","TenantId":"other-tenant"}""";
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(crossTenantJson);

        var payload = MakePayload(new()
        {
            ["Id"]      = Value.ForString(AuthorId),
            ["Name"]    = Value.ForString("Alice Updated"),
            ["OwnerId"] = Value.ForString("someone-else")
        });
        var act = () => _sut.Update(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task Update_AttemptingToChangeTenantField_ThrowsPermissionDenied()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema());
        var ownedJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","OwnerId":"test-user","TenantId":"test-tenant"}""";
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(ownedJson);

        var payload = MakePayload(new()
        {
            ["Id"]       = Value.ForString(AuthorId),
            ["Name"]     = Value.ForString("Alice Updated"),
            ["OwnerId"]  = Value.ForString("test-user"),
            ["TenantId"] = Value.ForString("other-tenant")
        });
        var act = () => _sut.Update(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        ex.Which.Status.Detail.Should().Contain("immutable");
    }

    [Fact]
    public async Task Update_WithBypassRoleCaller_AttemptingToChangeTenantField_ThrowsPermissionDenied()
    {
        // Tenant is strictly additive: a CanWriteAll bypass role must not exempt the caller
        // from the tenant-immutability check.
        await _registry.RegisterAsync(OwnedAuthorSchema(withBypassRole: true));
        var ownedJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","OwnerId":"someone-else","TenantId":"test-tenant"}""";
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(ownedJson);

        var payload = MakePayload(new()
        {
            ["Id"]       = Value.ForString(AuthorId),
            ["Name"]     = Value.ForString("Alice Updated"),
            ["OwnerId"]  = Value.ForString("someone-else"),
            ["TenantId"] = Value.ForString("other-tenant")
        });
        var act = () => _sut.Update(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        ex.Which.Status.Detail.Should().Contain("immutable");
    }

    [Fact]
    public async Task Update_WithRestrictedFieldInWritePayload_ThrowsInvalidArgument()
    {
        var schema = SchemaFixtures.AuthorSchema() with
        {
            Authorization = new Iverson.Api.Schema.AuthorizationRules(
                null,
                new List<Iverson.Api.Schema.RowPermission> { new("test-bypass", true, true, true) },
                new List<Iverson.Api.Schema.FieldPermission>
                {
                    new("Bio", new List<string>(), new List<string> { "premium" })
                })
        };
        await _registry.RegisterAsync(schema);
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(AuthorJson);

        var payload = MakePayload(new()
        {
            ["Id"]   = Value.ForString(AuthorId),
            ["Name"] = Value.ForString("Alice"),
            ["Bio"]  = Value.ForString("Writer")
        });
        var act = () => _sut.Update(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("someone-else")]
    public async Task Update_ForOrdinaryCaller_WhenRowDoesNotExistYet_ForceSetsOwnerFieldToActingUserSub(string? clientSuppliedOwnerId)
    {
        await _registry.RegisterAsync(OwnedAuthorSchema(withBypassRole: false));
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns((string?)null);

        var fields = new Dictionary<string, Value>
        {
            ["Id"]   = Value.ForString(AuthorId),
            ["Name"] = Value.ForString("Alice")
        };
        if (clientSuppliedOwnerId is not null)
            fields["OwnerId"] = Value.ForString(clientSuppliedOwnerId);
        var payload = MakePayload(fields);

        var response = await _sut.Update(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        response.Success.Should().BeTrue();
        response.Data.Fields["OwnerId"].StringValue.Should().Be("test-user");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("someone-else")]
    public async Task Update_WithBypassRole_WhenRowDoesNotExistYet_LeavesOwnerFieldUntouched(string? clientSuppliedOwnerId)
    {
        await _registry.RegisterAsync(OwnedAuthorSchema(withBypassRole: true));
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns((string?)null);

        var fields = new Dictionary<string, Value>
        {
            ["Id"]   = Value.ForString(AuthorId),
            ["Name"] = Value.ForString("Alice")
        };
        if (clientSuppliedOwnerId is not null)
            fields["OwnerId"] = Value.ForString(clientSuppliedOwnerId);
        var payload = MakePayload(fields);

        var response = await _sut.Update(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        response.Success.Should().BeTrue();
        if (clientSuppliedOwnerId is null)
            response.Data.Fields.Should().NotContainKey("OwnerId");
        else
            response.Data.Fields["OwnerId"].StringValue.Should().Be(clientSuppliedOwnerId);
    }

    [Fact]
    public async Task Update_WithNonBypassCaller_AttemptingToChangeOwnerField_ThrowsPermissionDenied()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema(withBypassRole: false));
        var ownedJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","OwnerId":"test-user","TenantId":"test-tenant"}""";
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(ownedJson);

        var payload = MakePayload(new()
        {
            ["Id"]      = Value.ForString(AuthorId),
            ["Name"]    = Value.ForString("Alice Updated"),
            ["OwnerId"] = Value.ForString("someone-else")
        });
        var act = () => _sut.Update(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        ex.Which.Status.Detail.Should().Contain("immutable");
    }

    [Fact]
    public async Task Update_WithBypassRoleCaller_AttemptingToChangeOwnerField_ThrowsPermissionDenied()
    {
        // CDR-fixed case: bypass callers have decision.OwnerFieldName == null (since ownership
        // is not required for them), so the immutability check must source the owner field name
        // from schema.Authorization?.OwnerField, not decision.OwnerFieldName, or this would never fire.
        await _registry.RegisterAsync(OwnedAuthorSchema(withBypassRole: true));
        var ownedJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","OwnerId":"someone-else","TenantId":"test-tenant"}""";
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(ownedJson);

        var payload = MakePayload(new()
        {
            ["Id"]      = Value.ForString(AuthorId),
            ["Name"]    = Value.ForString("Alice Updated"),
            ["OwnerId"] = Value.ForString("yet-another-user")
        });
        var act = () => _sut.Update(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        ex.Which.Status.Detail.Should().Contain("immutable");
    }

    // ── Post/Update audit logging ────────────────────────────────────────────

    [Fact]
    public async Task Post_AccessDenied_LogsAuditDeniedWithAccessDenied()
    {
        var schema = SchemaFixtures.AuthorSchema() with { Authorization = null };
        await _registry.RegisterAsync(schema);

        var payload = MakePayload(new() { ["Name"] = Value.ForString("Alice") });
        var act = () => _sut.Post(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>();
        AssertAuditLogged("AccessDenied");
    }

    [Fact]
    public async Task Update_TenantMismatch_LogsAuditDeniedWithTenantMismatch()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema());
        var crossTenantJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","OwnerId":"test-user","TenantId":"other-tenant"}""";
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(crossTenantJson);

        var payload = MakePayload(new()
        {
            ["Id"]      = Value.ForString(AuthorId),
            ["Name"]    = Value.ForString("Alice Updated"),
            ["OwnerId"] = Value.ForString("test-user")
        });
        var act = () => _sut.Update(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>();
        AssertAuditLogged("TenantMismatch");
    }

    [Fact]
    public async Task Update_TenantImmutable_LogsAuditDeniedWithTenantImmutable()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema());
        var ownedJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","OwnerId":"test-user","TenantId":"test-tenant"}""";
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(ownedJson);

        var payload = MakePayload(new()
        {
            ["Id"]       = Value.ForString(AuthorId),
            ["Name"]     = Value.ForString("Alice Updated"),
            ["OwnerId"]  = Value.ForString("test-user"),
            ["TenantId"] = Value.ForString("other-tenant")
        });
        var act = () => _sut.Update(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>();
        AssertAuditLogged("TenantImmutable");
    }

    [Fact]
    public async Task Update_OwnerMismatch_LogsAuditDeniedWithOwnerMismatch()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema());
        var ownedJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","OwnerId":"someone-else","TenantId":"test-tenant"}""";
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(ownedJson);

        var payload = MakePayload(new()
        {
            ["Id"]      = Value.ForString(AuthorId),
            ["Name"]    = Value.ForString("Alice Updated"),
            ["OwnerId"] = Value.ForString("someone-else")
        });
        var act = () => _sut.Update(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>();
        AssertAuditLogged("OwnerMismatch");
    }

    [Fact]
    public async Task Update_OwnerImmutable_LogsAuditDeniedWithOwnerImmutable()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema(withBypassRole: false));
        var ownedJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","OwnerId":"test-user","TenantId":"test-tenant"}""";
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(ownedJson);

        var payload = MakePayload(new()
        {
            ["Id"]      = Value.ForString(AuthorId),
            ["Name"]    = Value.ForString("Alice Updated"),
            ["OwnerId"] = Value.ForString("someone-else")
        });
        var act = () => _sut.Update(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>();
        AssertAuditLogged("OwnerImmutable");
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_WhenEntityExists_DeletesFromSqlAndEmitsEvent()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(AuthorJson);

        EntityEvent? evt = null;
        _events
            .When(e => e.ProduceAsync(
                EntityTopics.Events,
                Arg.Any<string>(),
                Arg.Any<EntityEvent>()))
            .Do(call => evt = call.ArgAt<EntityEvent>(2));

        var response = await _sut.Delete(
            new MappingDeleteRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        response.Success.Should().BeTrue();
        await _entities.Received(1).DeleteAsync(
            Arg.Any<IDbTransactionContext>(),
            Arg.Is<TableSchema>(s => s.TableName == "authors"),
            AuthorId,
            Arg.Any<bool>(),
            Arg.Any<string?>());
        evt!.TypeName.Should().Be("Author");
        evt.Key.Should().Be(AuthorId);
        evt.EventType.Should().Be(EntityEventType.Deleted);
    }

    [Fact]
    public async Task Delete_WhenEntityNotFound_ReturnsFailureWithoutEmittingEvent()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns((string?)null);

        var response = await _sut.Delete(
            new MappingDeleteRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        response.Success.Should().BeFalse();
        response.Error.Should().Contain("not found");
        await _events.DidNotReceive().ProduceAsync(
            EntityTopics.Events, Arg.Any<string>(), Arg.Any<EntityEvent>());
    }

    [Fact]
    public async Task Delete_InsertsDeleteOutboxRowInSameTransactionAsDelete_WithEventTypeAndSnapshotPayload()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(AuthorJson);

        var capturedWork = default(Func<IDbTransactionContext, Task>);
        _txRunner
            .ExecuteInTransactionAsync(
                Arg.Do<Func<IDbTransactionContext, Task>>(w => capturedWork = w))
            .Returns(Task.CompletedTask);

        await _sut.Delete(
            new MappingDeleteRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        capturedWork.Should().NotBeNull();

        // Execute the captured transactional work against a fake transaction context,
        // recording both the SQL and the bound parameter object for each statement it issues
        // directly (the entity delete itself now goes through _entities.DeleteAsync, which is
        // its own unit under EntityRepositoryTests — verified separately below), so we can
        // assert the outbox row is inserted as EventType='Deleted' with the pre-delete JSON
        // snapshot as its Payload — not merely that some INSERT happened.
        var calls = new List<(string Sql, object? Params)>();
        var fakeTx = Substitute.For<IDbTransactionContext>();
        fakeTx.ExecuteAsync(Arg.Do<string>(sql => { }), Arg.Any<object?>()).Returns(0);
        fakeTx
            .When(t => t.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()))
            .Do(call => calls.Add((call.ArgAt<string>(0), call.ArgAt<object?>(1))));

        await capturedWork!(fakeTx);

        await _entities.Received(1).DeleteAsync(
            fakeTx, Arg.Is<TableSchema>(s => s.TableName == "authors"), AuthorId,
            Arg.Any<bool>(), Arg.Any<string?>());

        var outboxCall = calls.Should().ContainSingle(
            c => c.Sql.Contains($"INSERT INTO \"{ReconciliationSchema.TableName}\"")).Subject;
        outboxCall.Sql.Should().Contain("'Deleted'");

        var payloadProp = outboxCall.Params!.GetType().GetProperty("Payload");
        payloadProp.Should().NotBeNull();
        payloadProp!.GetValue(outboxCall.Params).Should().Be(AuthorJson);
    }

    [Fact]
    public async Task Delete_WhenSchemaNotRegistered_ThrowsFailedPrecondition()
    {
        var act = () => _sut.Delete(
            new MappingDeleteRequest { TypeName = "Ghost", Key = AuthorId },
            TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
    }

    // ── Delete authorization ──────────────────────────────────────────────────

    [Fact]
    public async Task Delete_WithNoAuthorizationRulesConfigured_ReturnsNotFound()
    {
        var schema = SchemaFixtures.AuthorSchema() with { Authorization = null };
        await _registry.RegisterAsync(schema);
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(AuthorJson);

        var response = await _sut.Delete(
            new MappingDeleteRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        response.Success.Should().BeFalse();
        response.Error.Should().Contain("not found");
        await _events
            .DidNotReceive()
            .ProduceAsync(
                EntityTopics.Events,
                Arg.Any<string>(),
                Arg.Any<EntityEvent>());
    }

    [Fact]
    public async Task Delete_WithNoActingUser_ReturnsNotFound()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(AuthorJson);
        _actingUserAccessor.ActingUser = null;

        var response = await _sut.Delete(
            new MappingDeleteRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        response.Success.Should().BeFalse();
        response.Error.Should().Contain("not found");
        await _events.DidNotReceive()
            .ProduceAsync(
                EntityTopics.Events,
                Arg.Any<string>(),
                Arg.Any<EntityEvent>());
    }

    [Fact]
    public async Task Delete_WithMatchingOwner_ReturnsSuccess()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema());
        var ownedJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","OwnerId":"test-user","TenantId":"test-tenant"}""";
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(ownedJson);

        EntityEvent? evt = null;
        _events
            .When(e => e.ProduceAsync(
                EntityTopics.Events,
                Arg.Any<string>(),
                Arg.Any<EntityEvent>()))
            .Do(call => evt = call.ArgAt<EntityEvent>(2));

        var response = await _sut.Delete(
            new MappingDeleteRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        response.Success.Should().BeTrue();
        await _entities.Received(1)
            .DeleteAsync(
                Arg.Any<IDbTransactionContext>(),
                Arg.Is<TableSchema>(s => s.TableName == "authors"),
                AuthorId,
                Arg.Any<bool>(),
                Arg.Any<string?>());
        evt!.TypeName.Should().Be("Author");
        evt.EventType.Should().Be(EntityEventType.Deleted);
    }

    [Fact]
    public async Task Delete_WithBypassRole_ReturnsSuccess_EvenWhenNotOwner()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema(withBypassRole: true));
        var ownedJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","OwnerId":"someone-else","TenantId":"test-tenant"}""";
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(ownedJson);

        EntityEvent? evt = null;
        _events
            .When(e => e.ProduceAsync(
                EntityTopics.Events,
                Arg.Any<string>(),
                Arg.Any<EntityEvent>()))
            .Do(call => evt = call.ArgAt<EntityEvent>(2));

        var response = await _sut.Delete(
            new MappingDeleteRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        response.Success.Should().BeTrue();
        await _entities.Received(1).DeleteAsync(
            Arg.Any<IDbTransactionContext>(),
            Arg.Is<TableSchema>(s => s.TableName == "authors"),
            AuthorId,
            Arg.Any<bool>(),
            Arg.Any<string?>());
        evt!.TypeName.Should().Be("Author");
        evt.EventType.Should().Be(EntityEventType.Deleted);
    }

    [Fact]
    public async Task Delete_WithNonMatchingOwner_ReturnsNotFound()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema());
        var ownedJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","OwnerId":"someone-else","TenantId":"test-tenant"}""";
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(ownedJson);

        var response = await _sut.Delete(
            new MappingDeleteRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        response.Success.Should().BeFalse();
        response.Error.Should().Contain("not found");
        await _events.DidNotReceive()
            .ProduceAsync(
                EntityTopics.Events,
                Arg.Any<string>(),
                Arg.Any<EntityEvent>());
    }

    [Fact]
    public async Task Delete_WithNonMatchingTenant_ReturnsNotFound()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        var crossTenantJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","Bio":"Writer","TenantId":"other-tenant"}""";
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(crossTenantJson);

        var response = await _sut.Delete(
            new MappingDeleteRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        response.Success.Should().BeFalse();
        response.Error.Should().Contain("not found");
        await _entities.DidNotReceive()
            .DeleteAsync(
                Arg.Any<IDbTransactionContext>(),
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>());
        await _events.DidNotReceive().ProduceAsync(
            EntityTopics.Events, Arg.Any<string>(), Arg.Any<EntityEvent>());
    }

    [Fact]
    public async Task Delete_WithBypassRoleAndNonMatchingTenant_ReturnsNotFound()
    {
        // Tenant is strictly additive: a CanDeleteAll bypass role must not exempt the caller
        // from the tenant boundary.
        await _registry.RegisterAsync(OwnedAuthorSchema(withBypassRole: true));
        var crossTenantJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","OwnerId":"someone-else","TenantId":"other-tenant"}""";
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(crossTenantJson);

        var response = await _sut.Delete(
            new MappingDeleteRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        response.Success.Should().BeFalse();
        response.Error.Should().Contain("not found");
        await _entities.DidNotReceive()
            .DeleteAsync(
                Arg.Any<IDbTransactionContext>(),
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>());
        await _events.DidNotReceive()
            .ProduceAsync(
                EntityTopics.Events,
                Arg.Any<string>(),
                Arg.Any<EntityEvent>());
    }

    // ── Delete audit logging ─────────────────────────────────────────────────

    [Fact]
    public async Task Delete_AccessDenied_LogsAuditDeniedWithAccessDenied()
    {
        var schema = SchemaFixtures.AuthorSchema() with { Authorization = null };
        await _registry.RegisterAsync(schema);
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(AuthorJson);

        await _sut.Delete(
            new MappingDeleteRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        AssertAuditLogged("AccessDenied");
    }

    [Fact]
    public async Task Delete_OwnerMismatch_LogsAuditDeniedWithOwnerMismatch()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema());
        var ownedJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","OwnerId":"someone-else","TenantId":"test-tenant"}""";
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(ownedJson);

        await _sut.Delete(
            new MappingDeleteRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        AssertAuditLogged("OwnerMismatch");
    }

    [Fact]
    public async Task Delete_TenantMismatch_LogsAuditDeniedWithTenantMismatch()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        var crossTenantJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","Bio":"Writer","TenantId":"other-tenant"}""";
        _entities
            .FetchByKeyAsync(
                Arg.Any<TableSchema>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<string?>())
            .Returns(crossTenantJson);

        await _sut.Delete(
            new MappingDeleteRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        AssertAuditLogged("TenantMismatch");
    }
}
