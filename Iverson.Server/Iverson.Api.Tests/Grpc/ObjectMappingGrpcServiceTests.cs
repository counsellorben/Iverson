using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Grpc;
using Iverson.Api.Reconciliation;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.Events;
using Iverson.Sql;
using Iverson.StarRocks;
using Iverson.Vector;
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
    private readonly IVectorSchemaManager _vector;
    private readonly IEventProducer _events;
    private readonly SchemaRegistry _registry;
    private readonly IEmbeddingService _embedding;
    private readonly IEngagementStoreSchemaManager _starRocks;
    private readonly ObjectMappingGrpcService _sut;

    private static readonly string AuthorId  = "11111111-0000-0000-0000-000000000001";
    private static readonly string ArticleId = "22222222-0000-0000-0000-000000000002";
    private static readonly string AuthorJson  = $$"""{"Id":"{{AuthorId}}","Name":"Alice","Bio":"Writer"}""";
    private static readonly string ArticleJson = $$"""{"Id":"{{ArticleId}}","Title":"Hello","Body":"World","AuthorId":"{{AuthorId}}"}""";

    public ObjectMappingGrpcServiceTests()
    {
        _sql      = Substitute.For<IRecordStoreQueryExecutor>();
        _entities = Substitute.For<IEntityRepository>();
        _vector   = Substitute.For<IVectorSchemaManager>();
        _events   = Substitute.For<IEventProducer>();

        _sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(1);
        _entities.FetchByColumnAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(Enumerable.Empty<string>()));

        _txRunner = Substitute.For<IRecordStoreTransactionRunner>();
        _txRunner.ExecuteInTransactionAsync(Arg.Any<Func<IDbTransactionContext, Task>>())
            .Returns(ci => ci.Arg<Func<IDbTransactionContext, Task>>()(Substitute.For<IDbTransactionContext>()));

        _schemaManager = Substitute.For<IRecordStoreSchemaManager>();

        _embedding = Substitute.For<IEmbeddingService>();
        _embedding.Dimension.Returns(768);
        _embedding.ModelId.Returns("nomic-embed-text");

        _starRocks = Substitute.For<IEngagementStoreSchemaManager>();
        _starRocks.ApplyTableAsync(Arg.Any<StarRocksTableSchema>()).Returns(Task.CompletedTask);

        _registry = new SchemaRegistry(new SchemaRegistryRepository(_sql), NullLogger<SchemaRegistry>.Instance);
        _sut = new ObjectMappingGrpcService(
            _entities, _txRunner, _schemaManager, _vector, _events, _registry, _embedding, _starRocks,
            new RelationValidator(_registry), new EntityKeyAccessor(),
            new OutboxWriter(ReconciliationSchema.TableName, _sql, _txRunner),
            NullLogger<ObjectMappingGrpcService>.Instance);
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
        Relations     = []
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
        _events.When(e => e.ProduceAsync(topic, Arg.Any<string>(), Arg.Any<EntityEvent>()))
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

        _txRunner.ExecuteInTransactionAsync(Arg.Any<Func<IDbTransactionContext, Task>>())
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
    public async Task RegisterSchema_WithInvalidOwnerField_ThrowsInvalidArgument()
    {
        var td = SimpleType("Widget", "Name");
        td.Authorization = new Client.Contracts.AuthorizationRules { OwnerField = "DoesNotExist" };

        var act = () => _sut.RegisterSchema(
            new SchemaRequest { RootType = td }, TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task RegisterSchema_CallsApplyTableAsync_WithMatchingTableName()
    {
        var request = new SchemaRequest { RootType = SimpleType("Author", "Name") };

        await _sut.RegisterSchema(request, TestServerCallContext.Create());

        await _starRocks.Received(1).ApplyTableAsync(
            Arg.Is<StarRocksTableSchema>(s => s.TableName == "authors"));
    }

    [Fact]
    public async Task RegisterSchema_WithSimpleEntity_ReturnsSuccessAndPersistsInRegistry()
    {
        var request = new SchemaRequest { RootType = SimpleType("Tag", "Label") };

        var response = await _sut.RegisterSchema(request, TestServerCallContext.Create());

        response.Success.Should().BeTrue();
        response.Registered.Should().Contain("Tag");
        _registry.Get("Tag").Should().NotBeNull();
    }

    [Fact]
    public async Task RegisterSchema_WithManyToOneRelation_DoesNotThrow()
    {
        var td = SimpleType("Comment", "Body", "ArticleId");
        td.Relations.Add(new Client.Contracts.RelationDescriptor
        {
            PropertyName = "Article",
            Kind         = Client.Contracts.RelationKind.ManyToOne,
            RelatedType  = "Article",
            ForeignKey   = "ArticleId"
        });

        var response = await _sut.RegisterSchema(
            new SchemaRequest { RootType = td }, TestServerCallContext.Create());

        response.Success.Should().BeTrue();
        response.Registered.Should().Contain("Comment");
    }

    [Fact]
    public async Task RegisterSchema_WithManyToManyRelation_DoesNotThrow()
    {
        var td = new TypeDescriptor { TypeName = "Post" };
        td.Properties.Add(new PropertyDescriptor { Name = "Id",     ClrType = ClrType.ClrGuid,   IsKey = true });
        td.Properties.Add(new PropertyDescriptor { Name = "TagIds", ClrType = ClrType.ClrGuid,   IsArray = true });
        td.Relations.Add(new Client.Contracts.RelationDescriptor
        {
            PropertyName = "Tags",
            Kind         = Client.Contracts.RelationKind.ManyToMany,
            RelatedType  = "Tag",
            ForeignKey   = "TagIds"
        });

        var response = await _sut.RegisterSchema(
            new SchemaRequest { RootType = td }, TestServerCallContext.Create());

        response.Success.Should().BeTrue();
    }

    [Fact]
    public async Task RegisterSchema_WithDependents_RegistersAllTypes()
    {
        var request = new SchemaRequest
        {
            RootType = SimpleType("Article", "Title"),
            Dependents = { SimpleType("Author", "Name") }
        };

        var response = await _sut.RegisterSchema(request, TestServerCallContext.Create());

        response.Registered.Should().Contain("Article").And.Contain("Author");
    }

    [Fact]
    public async Task RegisterSchema_SetsVectorDimAndModelId_FromEmbeddingService()
    {
        var typeDesc = new TypeDescriptor { TypeName = "EmbeddableDoc" };
        typeDesc.Properties.Add(new PropertyDescriptor
        {
            Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true
        });
        typeDesc.Properties.Add(new PropertyDescriptor
        {
            Name    = "Content",
            ClrType = ClrType.ClrString,
            IsEmbedding = true,
            VectorDim   = 0,
            ModelId     = string.Empty
        });

        var request  = new SchemaRequest { RootType = typeDesc };
        var response = await _sut.RegisterSchema(request, Substitute.For<ServerCallContext>());

        response.Success.Should().BeTrue();
        var schema = _registry.Get("EmbeddableDoc")!;
        schema.VectorFields.Should().ContainSingle();
        schema.VectorFields[0].Dimension.Should().Be(768);
        schema.VectorFields[0].ModelId.Should().Be("nomic-embed-text");
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
    public async Task Post_WithExistingKey_PreservesClientKey()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _sql.QuerySingleOrDefaultAsync<string>(Arg.Any<string>(), Arg.Any<object?>())
            .Returns(AuthorJson);

        EntityEvent? evt = null;
        _events.When(e => e.ProduceAsync(EntityTopics.Events, Arg.Any<string>(), Arg.Any<EntityEvent>()))
               .Do(call => evt = call.ArgAt<EntityEvent>(2));

        var payload = MakePayload(new()
        {
            ["Id"]   = Value.ForString(AuthorId),
            ["Name"] = Value.ForString("Alice")
        });
        await _sut.Post(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        evt!.Key.Should().Be(AuthorId);
    }

    [Fact]
    public async Task Post_ExecutesUpsertSql_DirectlyToPostgres()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        var executedSql = CaptureTransactionalSql();

        var payload = MakePayload(new()
        {
            ["Id"]   = Value.ForString(AuthorId),
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
            ["Id"]       = Value.ForString(ArticleId),
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
            ["Id"]   = Value.ForString(AuthorId),
            ["Name"] = Value.ForString("Alice")
        });
        await _sut.Post(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        evt.Should().NotBeNull();
        evt!.TypeName.Should().Be("Author");
        evt.Key.Should().Be(AuthorId);
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
        var sentKey = Guid.NewGuid().ToString();
        var payload = MakePayload(schema.KeyColumn.Name, sentKey);
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
            ["Id"]       = Value.ForString(ArticleId),
            ["Title"]    = Value.ForString("Hello"),
            ["AuthorId"] = Value.ForString("not-a-guid")
        });
        var act = () => _sut.Post(
            new MappingWriteRequest { TypeName = "Article", Payload = payload },
            TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    // ── Get ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_WhenEntityExists_ReturnsSuccessWithParsedData()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
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
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
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
    public async Task Get_WithDepth1_ResolvesManyToOneRelation()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        _entities.FetchByKeyAsync(
                Arg.Is<TableSchema>(s => s.TableName == "articles"), Arg.Any<string>())
            .Returns(ArticleJson);
        _entities.FetchByKeyAsync(
                Arg.Is<TableSchema>(s => s.TableName == "authors"), Arg.Any<string>())
            .Returns(AuthorJson);

        var response = await _sut.Get(
            new MappingGetRequest { TypeName = "Article", Key = ArticleId, Depth = 1 },
            TestServerCallContext.Create());

        response.Success.Should().BeTrue();
        response.Data.Fields.Should().ContainKey("Author");
        response.Data.Fields["Author"].StructValue.Fields["Name"].StringValue.Should().Be("Alice");
    }

    [Fact]
    public async Task Get_WithDepth0_DoesNotResolveRelations()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
            .Returns(ArticleJson);

        var response = await _sut.Get(
            new MappingGetRequest { TypeName = "Article", Key = ArticleId, Depth = 0 },
            TestServerCallContext.Create());

        response.Success.Should().BeTrue();
        response.Data.Fields.Should().NotContainKey("Author");
        await _entities.Received(1).FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>());
    }

    // ── ResolveManyToMany ─────────────────────────────────────────────────────

    [Fact]
    public async Task Get_WithManyToManyRelation_IssuesSingleBatchQuery()
    {
        var postId = "33333333-0000-0000-0000-000000000003";
        var tagId1 = "44444444-0000-0000-0000-000000000004";
        var tagId2 = "44444444-0000-0000-0000-000000000005";

        await _registry.RegisterAsync(SchemaFixtures.PostWithTagsSchema());
        await _registry.RegisterAsync(SchemaFixtures.TagSchema());

        var postJson = $$"""{"Id":"{{postId}}","Title":"Hello","TagIds":["{{tagId1}}","{{tagId2}}"]}""";
        _entities.FetchByKeyAsync(
                Arg.Is<TableSchema>(s => s.TableName == "posts"), Arg.Any<string>())
            .Returns(postJson);

        var tag1Json = $$"""{"Id":"{{tagId1}}","Label":"dotnet"}""";
        var tag2Json = $$"""{"Id":"{{tagId2}}","Label":"csharp"}""";
        _entities.FetchManyByKeysAsync(Arg.Any<TableSchema>(), Arg.Any<IReadOnlyList<string>>())
            .Returns(new[] { new KeyedRow(tagId1, tag1Json), new KeyedRow(tagId2, tag2Json) });

        var response = await _sut.Get(
            new MappingGetRequest { TypeName = "Post", Key = postId, Depth = 1 },
            MakeContext());

        await _entities.Received(1).FetchManyByKeysAsync(
            Arg.Any<TableSchema>(),
            Arg.Is<IReadOnlyList<string>>(keys => keys.Count == 2));

        response.Success.Should().BeTrue();
        response.Data.Fields["Tags"].ListValue.Values.Should().HaveCount(2);
    }

    // ── Update ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_WithValidKey_EmitsUpdatedEvent()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        EntityEvent? evt = null;
        _events.When(e => e.ProduceAsync(EntityTopics.Events, Arg.Any<string>(), Arg.Any<EntityEvent>()))
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
        _txRunner.ExecuteInTransactionAsync(Arg.Do<Func<IDbTransactionContext, Task>>(w => capturedWork = w))
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

    // ── Delete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_WhenEntityExists_DeletesFromSqlAndEmitsEvent()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
            .Returns(AuthorJson);

        EntityEvent? evt = null;
        _events.When(e => e.ProduceAsync(EntityTopics.Events, Arg.Any<string>(), Arg.Any<EntityEvent>()))
               .Do(call => evt = call.ArgAt<EntityEvent>(2));

        var response = await _sut.Delete(
            new MappingDeleteRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        response.Success.Should().BeTrue();
        await _entities.Received(1).DeleteAsync(
            Arg.Any<IDbTransactionContext>(),
            Arg.Is<TableSchema>(s => s.TableName == "authors"),
            AuthorId);
        evt!.TypeName.Should().Be("Author");
        evt.Key.Should().Be(AuthorId);
        evt.EventType.Should().Be(EntityEventType.Deleted);
    }

    [Fact]
    public async Task Delete_WhenEntityNotFound_ReturnsFailureWithoutEmittingEvent()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
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
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
            .Returns(AuthorJson);

        var capturedWork = default(Func<IDbTransactionContext, Task>);
        _txRunner.ExecuteInTransactionAsync(Arg.Do<Func<IDbTransactionContext, Task>>(w => capturedWork = w))
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
        fakeTx.When(t => t.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()))
              .Do(call => calls.Add((call.ArgAt<string>(0), call.ArgAt<object?>(1))));

        await capturedWork!(fakeTx);

        await _entities.Received(1).DeleteAsync(
            fakeTx, Arg.Is<TableSchema>(s => s.TableName == "authors"), AuthorId);

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
}
