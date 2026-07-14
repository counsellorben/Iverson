using System.Text.Json;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Authorization;
using Iverson.Api.Grpc;
using Iverson.Api.Reconciliation;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Iverson.Events;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

public class ObjectPersistenceGrpcServiceTests
{
    private readonly IEventProducer _events;
    private readonly IRecordStoreQueryExecutor _sql;
    private readonly IRecordStoreTransactionRunner _txRunner;
    private readonly SchemaRegistry _registry;
    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();
    private readonly IActingUserAccessor _actingUserAccessor;
    private readonly IRowFieldAuthorizationEvaluator _authEvaluator = new RowFieldAuthorizationEvaluator();
    private readonly ObjectPersistenceGrpcService _sut;

    public ObjectPersistenceGrpcServiceTests()
    {
        _events = Substitute.For<IEventProducer>();

        _sql = Substitute.For<IRecordStoreQueryExecutor>();
        _sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);

        // NSubstitute's auto-value for an unconfigured Task<string?> member is Task.FromResult(""),
        // not null — default every FetchByKeyAsync call to "row not found" so Update's new
        // pre-fetch (Task 6) doesn't try to JSON-parse an empty string in tests that don't care
        // about the pre-existing-row branch. Individual tests override this with .Returns(...)
        // for the specific TableSchema/key they need.
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
            .Returns((string?)null);

        _txRunner = Substitute.For<IRecordStoreTransactionRunner>();
        _txRunner.ExecuteInTransactionAsync(Arg.Any<Func<IDbTransactionContext, Task>>())
            .Returns(ci => ci.Arg<Func<IDbTransactionContext, Task>>()(Substitute.For<IDbTransactionContext>()));

        _registry = new SchemaRegistry(new SchemaRegistryRepository(_sql), NullLogger<SchemaRegistry>.Instance);
        _actingUserAccessor = new ActingUserAccessor
            { ActingUser = ActingUserFixtures.Principal("test-user", "test-bypass") };
        _sut = new ObjectPersistenceGrpcService(
            _events, _registry,
            new RelationValidator(_registry), new EntityKeyAccessor(),
            new OutboxWriter(ReconciliationSchema.TableName, _sql, _txRunner),
            NullLogger<ObjectPersistenceGrpcService>.Instance,
            _entities, _actingUserAccessor, _authEvaluator);
    }

    private static Struct MakePayload(Dictionary<string, Value> fields)
    {
        var s = new Struct();
        foreach (var (k, v) in fields) s.Fields[k] = v;
        return s;
    }

    private static SchemaDescriptor OwnedAuthorSchema(bool withBypassRole = false) => new()
    {
        TypeName       = "Author",
        TableName      = "authors",
        CollectionName = null,
        KeyColumn      = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns  =
        [
            new ColumnDescriptor("Name", "text", false),
            new ColumnDescriptor("OwnerId", "text", false)
        ],
        FkColumns     = [],
        VectorFields  = [],
        ChunkFields   = [],
        Relations     = [],
        Authorization = new Iverson.Api.Schema.AuthorizationRules(
            "OwnerId",
            withBypassRole
                ? new List<Iverson.Api.Schema.RowPermission> { new("test-bypass", true, true, true) }
                : new List<Iverson.Api.Schema.RowPermission>(),
            new List<Iverson.Api.Schema.FieldPermission>())
    };

    private EntityEvent? CaptureFireAndForgetEvent(string topic)
    {
        EntityEvent? captured = null;
        _events.When(e => e.ProduceAsync(topic, Arg.Any<string>(), Arg.Any<EntityEvent>()))
               .Do(call => captured = call.ArgAt<EntityEvent>(2));
        return captured; // populated after sut call — caller must read after invoking sut
    }

    /// <summary>
    /// Configures <see cref="_sql"/>'s <c>ExecuteInTransactionAsync</c> to actually invoke the
    /// captured transactional work against a fake <see cref="IDbTransactionContext"/>, recording
    /// every SQL statement issued inside it. Used by tests that need to assert on what happens
    /// inside the upsert+outbox transaction (as opposed to tests that only care about the
    /// opportunistic publish, for which the default unconfigured no-op is sufficient).
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

    [Fact]
    public async Task Post_ReturnsSuccess_WithGeneratedKey_WhenKeyAbsent()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        var payload = MakePayload(new() { ["Name"] = Value.ForString("Alice") });
        var request = new PersistRequest { TypeName = "Author", Payload = payload };

        var response = await _sut.Post(request, TestServerCallContext.Create());

        response.Success.Should().BeTrue();
        Guid.TryParse(response.Key, out _).Should().BeTrue();
    }

    [Fact]
    public async Task Post_IgnoresClientProvidedKey_AndAssignsServerKey()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        var clientGuid = Guid.NewGuid().ToString();
        var payload = MakePayload(new()
        {
            ["Id"]   = Value.ForString(clientGuid),
            ["Name"] = Value.ForString("Bob")
        });
        var request = new PersistRequest { TypeName = "Author", Payload = payload };

        var response = await _sut.Post(request, TestServerCallContext.Create());

        response.Key.Should().NotBe(clientGuid);
        Guid.TryParse(response.Key, out _).Should().BeTrue();
    }

    [Fact]
    public async Task Post_ExecutesSqlUpsert_WithPayloadJson()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        var executedSql = CaptureTransactionalSql();
        var payload = MakePayload(new() { ["Name"] = Value.ForString("Alice") });

        await _sut.Post(new PersistRequest { TypeName = "Author", Payload = payload }, TestServerCallContext.Create());

        executedSql.Should().Contain(s => s.Contains("json_populate_record"));
    }

    [Fact]
    public async Task Post_InsertsReconciliationQueueRowInSameTransactionAsUpsert()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        var capturedWork = default(Func<IDbTransactionContext, Task>);
        _txRunner.ExecuteInTransactionAsync(Arg.Do<Func<IDbTransactionContext, Task>>(w => capturedWork = w))
            .Returns(Task.CompletedTask);

        var request = new PersistRequest { TypeName = "Article", Payload = new Struct() };
        request.Payload.Fields["Title"] = Value.ForString("Test");
        request.Payload.Fields["AuthorId"] = Value.ForString(Guid.NewGuid().ToString());

        await _sut.Post(request, TestServerCallContext.Create());

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
    public async Task Post_PublishesFireAndForget_WithEngagementTarget()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        EntityEvent? captured = null;
        _events.When(e => e.ProduceAsync(EntityTopics.Events, Arg.Any<string>(), Arg.Any<EntityEvent>()))
               .Do(call => captured = call.ArgAt<EntityEvent>(2));

        var payload = MakePayload(new() { ["Name"] = Value.ForString("Alice") });
        await _sut.Post(new PersistRequest { TypeName = "Author", Payload = payload }, TestServerCallContext.Create());

        captured.Should().NotBeNull();
        captured!.TargetStores.Should().Be(StoreTarget.Engagement);
    }

    [Fact]
    public async Task Post_IncludesEngagement_WhenSchemaIsEngagementEligible()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        EntityEvent? captured = null;
        _events.When(e => e.ProduceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EntityEvent>()))
               .Do(call => captured = call.ArgAt<EntityEvent>(2));

        var payload = MakePayload(new() { ["Name"] = Value.ForString("Alice") });
        await _sut.Post(new PersistRequest { TypeName = "Author", Payload = payload }, TestServerCallContext.Create());

        captured!.TargetStores.HasFlag(StoreTarget.Engagement).Should().BeTrue();
    }

    [Fact]
    public async Task Post_ExcludesEngagement_WhenSchemaHasOneToMany()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleWithOneToManySchema());

        EntityEvent? captured = null;
        _events.When(e => e.ProduceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EntityEvent>()))
               .Do(call => captured = call.ArgAt<EntityEvent>(2));

        var payload = MakePayload(new()
        {
            ["Title"]    = Value.ForString("My Article"),
            ["AuthorId"] = Value.ForString(Guid.NewGuid().ToString())
        });
        await _sut.Post(new PersistRequest { TypeName = "Article", Payload = payload }, TestServerCallContext.Create());

        captured!.TargetStores.HasFlag(StoreTarget.Engagement).Should().BeFalse();
    }

    [Fact]
    public async Task Post_IncludesIntelligence_WhenVectorFieldsPresent()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleSchema());

        EntityEvent? captured = null;
        _events.When(e => e.ProduceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EntityEvent>()))
               .Do(call => captured = call.ArgAt<EntityEvent>(2));

        var payload = MakePayload(new()
        {
            ["Title"]    = Value.ForString("Test Article"),
            ["Body"]     = Value.ForString("Body text"),
            ["AuthorId"] = Value.ForString(Guid.NewGuid().ToString())
        });
        await _sut.Post(new PersistRequest { TypeName = "Article", Payload = payload }, TestServerCallContext.Create());

        captured!.TargetStores.HasFlag(StoreTarget.Intelligence).Should().BeTrue();
    }

    [Fact]
    public async Task Post_ExcludesIntelligence_WhenNoVectorFields()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        EntityEvent? captured = null;
        _events.When(e => e.ProduceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EntityEvent>()))
               .Do(call => captured = call.ArgAt<EntityEvent>(2));

        var payload = MakePayload(new() { ["Name"] = Value.ForString("Alice") });
        await _sut.Post(new PersistRequest { TypeName = "Author", Payload = payload }, TestServerCallContext.Create());

        captured!.TargetStores.HasFlag(StoreTarget.Intelligence).Should().BeFalse();
    }

    [Fact]
    public async Task Post_ThrowsRpcException_WhenSchemaNotRegistered()
    {
        var payload = MakePayload(new() { ["Name"] = Value.ForString("Alice") });
        var request = new PersistRequest { TypeName = "Ghost", Payload = payload };

        var act = async () => await _sut.Post(request, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.FailedPrecondition);
    }

    // ── Post authorization ───────────────────────────────────────────────────

    [Fact]
    public async Task Post_WithNoAuthorizationRulesConfigured_ThrowsPermissionDenied()
    {
        var schema = SchemaFixtures.AuthorSchema() with { Authorization = null };
        await _registry.RegisterAsync(schema);

        var payload = MakePayload(new() { ["Name"] = Value.ForString("Alice") });
        var request = new PersistRequest { TypeName = "Author", Payload = payload };

        var act = async () => await _sut.Post(request, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task Post_WithNoActingUser_ThrowsPermissionDenied()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _actingUserAccessor.ActingUser = null;

        var payload = MakePayload(new() { ["Name"] = Value.ForString("Alice") });
        var request = new PersistRequest { TypeName = "Author", Payload = payload };

        var act = async () => await _sut.Post(request, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.PermissionDenied);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("someone-else")]
    public async Task Post_ForOrdinaryCaller_ForceSetsOwnerFieldToActingUserSub(string? clientSuppliedOwnerId)
    {
        await _registry.RegisterAsync(OwnedAuthorSchema(withBypassRole: false));

        var fields = new Dictionary<string, Value> { ["Name"] = Value.ForString("Alice") };
        if (clientSuppliedOwnerId is not null)
            fields["OwnerId"] = Value.ForString(clientSuppliedOwnerId);
        var payload = MakePayload(fields);

        var response = await _sut.Post(
            new PersistRequest { TypeName = "Author", Payload = payload }, TestServerCallContext.Create());

        response.Success.Should().BeTrue();
        payload.Fields["OwnerId"].StringValue.Should().Be("test-user");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("someone-else")]
    public async Task Post_WithBypassRole_LeavesOwnerFieldUntouched(string? clientSuppliedOwnerId)
    {
        await _registry.RegisterAsync(OwnedAuthorSchema(withBypassRole: true));

        var fields = new Dictionary<string, Value> { ["Name"] = Value.ForString("Alice") };
        if (clientSuppliedOwnerId is not null)
            fields["OwnerId"] = Value.ForString(clientSuppliedOwnerId);
        var payload = MakePayload(fields);

        var response = await _sut.Post(
            new PersistRequest { TypeName = "Author", Payload = payload }, TestServerCallContext.Create());

        response.Success.Should().BeTrue();
        if (clientSuppliedOwnerId is null)
            payload.Fields.Should().NotContainKey("OwnerId");
        else
            payload.Fields["OwnerId"].StringValue.Should().Be(clientSuppliedOwnerId);
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
        var request = new PersistRequest { TypeName = "Author", Payload = payload };

        var act = async () => await _sut.Post(request, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument);
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

        var payload = MakePayload(new() { ["Name"] = Value.ForString("Alice") });

        var response = await _sut.Post(
            new PersistRequest { TypeName = "Author", Payload = payload }, TestServerCallContext.Create());

        response.Success.Should().BeTrue();
        payload.Fields["OwnerId"].StringValue.Should().Be("test-user");
    }

    [Fact]
    public async Task Update_ExecutesSqlUpsert_WithPayloadJson()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        var executedSql = CaptureTransactionalSql();
        var payload = MakePayload(new()
        {
            ["Id"]   = Value.ForString(Guid.NewGuid().ToString()),
            ["Name"] = Value.ForString("Alice")
        });

        await _sut.Update(new PersistRequest { TypeName = "Author", Payload = payload }, TestServerCallContext.Create());

        executedSql.Should().Contain(s => s.Contains("json_populate_record"));
    }

    [Fact]
    public async Task Update_ThrowsRpcException_WhenKeyMissing()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        var payload = MakePayload(new() { ["Name"] = Value.ForString("Alice") });
        var request = new PersistRequest { TypeName = "Author", Payload = payload };

        var act = async () => await _sut.Update(request, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task Update_ThrowsRpcException_WhenSchemaNotRegistered()
    {
        var payload = MakePayload(new()
        {
            ["Id"]   = Value.ForString(Guid.NewGuid().ToString()),
            ["Name"] = Value.ForString("Alice")
        });
        var request = new PersistRequest { TypeName = "Ghost", Payload = payload };

        var act = async () => await _sut.Update(request, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.FailedPrecondition);
    }

    // ── Update authorization ──────────────────────────────────────────────────

    [Fact]
    public async Task Update_WithNoAuthorizationRulesConfigured_ThrowsPermissionDenied()
    {
        var schema = SchemaFixtures.AuthorSchema() with { Authorization = null };
        await _registry.RegisterAsync(schema);

        var payload = MakePayload(new()
        {
            ["Id"]   = Value.ForString(Guid.NewGuid().ToString()),
            ["Name"] = Value.ForString("Alice Updated")
        });
        var request = new PersistRequest { TypeName = "Author", Payload = payload };

        var act = async () => await _sut.Update(request, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task Update_WithNoActingUser_ThrowsPermissionDenied()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _actingUserAccessor.ActingUser = null;

        var payload = MakePayload(new()
        {
            ["Id"]   = Value.ForString(Guid.NewGuid().ToString()),
            ["Name"] = Value.ForString("Alice Updated")
        });
        var request = new PersistRequest { TypeName = "Author", Payload = payload };

        var act = async () => await _sut.Update(request, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task Update_WithMatchingOwner_ReturnsSuccess()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema());
        var authorId = Guid.NewGuid().ToString();
        var ownedJson = $$"""{"Id":"{{authorId}}","Name":"Alice","OwnerId":"test-user"}""";
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
            .Returns(ownedJson);

        var payload = MakePayload(new()
        {
            ["Id"]      = Value.ForString(authorId),
            ["Name"]    = Value.ForString("Alice Updated"),
            ["OwnerId"] = Value.ForString("test-user")
        });
        var request = new PersistRequest { TypeName = "Author", Payload = payload };

        var response = await _sut.Update(request, TestServerCallContext.Create());

        response.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Update_WithBypassRole_ReturnsSuccess_EvenWhenNotOwner()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema(withBypassRole: true));
        var authorId = Guid.NewGuid().ToString();
        var ownedJson = $$"""{"Id":"{{authorId}}","Name":"Alice","OwnerId":"someone-else"}""";
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
            .Returns(ownedJson);

        var payload = MakePayload(new()
        {
            ["Id"]      = Value.ForString(authorId),
            ["Name"]    = Value.ForString("Alice Updated"),
            ["OwnerId"] = Value.ForString("someone-else")
        });
        var request = new PersistRequest { TypeName = "Author", Payload = payload };

        var response = await _sut.Update(request, TestServerCallContext.Create());

        response.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Update_WithNonMatchingOwner_ThrowsPermissionDenied()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema());
        var authorId = Guid.NewGuid().ToString();
        var ownedJson = $$"""{"Id":"{{authorId}}","Name":"Alice","OwnerId":"someone-else"}""";
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
            .Returns(ownedJson);

        var payload = MakePayload(new()
        {
            ["Id"]      = Value.ForString(authorId),
            ["Name"]    = Value.ForString("Alice Updated"),
            ["OwnerId"] = Value.ForString("someone-else")
        });
        var request = new PersistRequest { TypeName = "Author", Payload = payload };

        var act = async () => await _sut.Update(request, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.PermissionDenied);
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
        var authorId = Guid.NewGuid().ToString();
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
            .Returns($$"""{"Id":"{{authorId}}","Name":"Alice","Bio":"Writer"}""");

        var payload = MakePayload(new()
        {
            ["Id"]   = Value.ForString(authorId),
            ["Name"] = Value.ForString("Alice"),
            ["Bio"]  = Value.ForString("Writer")
        });
        var request = new PersistRequest { TypeName = "Author", Payload = payload };

        var act = async () => await _sut.Update(request, TestServerCallContext.Create());

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("someone-else")]
    public async Task Update_ForOrdinaryCaller_WhenRowDoesNotExistYet_ForceSetsOwnerFieldToActingUserSub(string? clientSuppliedOwnerId)
    {
        await _registry.RegisterAsync(OwnedAuthorSchema(withBypassRole: false));
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
            .Returns((string?)null);

        var fields = new Dictionary<string, Value>
        {
            ["Id"]   = Value.ForString(Guid.NewGuid().ToString()),
            ["Name"] = Value.ForString("Alice")
        };
        if (clientSuppliedOwnerId is not null)
            fields["OwnerId"] = Value.ForString(clientSuppliedOwnerId);
        var payload = MakePayload(fields);
        var request = new PersistRequest { TypeName = "Author", Payload = payload };

        var response = await _sut.Update(request, TestServerCallContext.Create());

        response.Success.Should().BeTrue();
        payload.Fields["OwnerId"].StringValue.Should().Be("test-user");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("someone-else")]
    public async Task Update_WithBypassRole_WhenRowDoesNotExistYet_LeavesOwnerFieldUntouched(string? clientSuppliedOwnerId)
    {
        await _registry.RegisterAsync(OwnedAuthorSchema(withBypassRole: true));
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
            .Returns((string?)null);

        var fields = new Dictionary<string, Value>
        {
            ["Id"]   = Value.ForString(Guid.NewGuid().ToString()),
            ["Name"] = Value.ForString("Alice")
        };
        if (clientSuppliedOwnerId is not null)
            fields["OwnerId"] = Value.ForString(clientSuppliedOwnerId);
        var payload = MakePayload(fields);
        var request = new PersistRequest { TypeName = "Author", Payload = payload };

        var response = await _sut.Update(request, TestServerCallContext.Create());

        response.Success.Should().BeTrue();
        if (clientSuppliedOwnerId is null)
            payload.Fields.Should().NotContainKey("OwnerId");
        else
            payload.Fields["OwnerId"].StringValue.Should().Be(clientSuppliedOwnerId);
    }

    [Fact]
    public async Task Update_WithNonBypassCaller_AttemptingToChangeOwnerField_ThrowsPermissionDenied()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema(withBypassRole: false));
        var authorId = Guid.NewGuid().ToString();
        var ownedJson = $$"""{"Id":"{{authorId}}","Name":"Alice","OwnerId":"test-user"}""";
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
            .Returns(ownedJson);

        var payload = MakePayload(new()
        {
            ["Id"]      = Value.ForString(authorId),
            ["Name"]    = Value.ForString("Alice Updated"),
            ["OwnerId"] = Value.ForString("someone-else")
        });
        var request = new PersistRequest { TypeName = "Author", Payload = payload };

        var act = async () => await _sut.Update(request, TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.Status.StatusCode.Should().Be(StatusCode.PermissionDenied);
        ex.Which.Status.Detail.Should().Contain("immutable");
    }

    [Fact]
    public async Task Update_WithBypassRoleCaller_AttemptingToChangeOwnerField_ThrowsPermissionDenied()
    {
        // CDR-fixed case: bypass callers have decision.OwnerFieldName == null (since ownership
        // is not required for them), so the immutability check must source the owner field name
        // from schema.Authorization?.OwnerField, not decision.OwnerFieldName, or this would never fire.
        await _registry.RegisterAsync(OwnedAuthorSchema(withBypassRole: true));
        var authorId = Guid.NewGuid().ToString();
        var ownedJson = $$"""{"Id":"{{authorId}}","Name":"Alice","OwnerId":"someone-else"}""";
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>())
            .Returns(ownedJson);

        var payload = MakePayload(new()
        {
            ["Id"]      = Value.ForString(authorId),
            ["Name"]    = Value.ForString("Alice Updated"),
            ["OwnerId"] = Value.ForString("yet-another-user")
        });
        var request = new PersistRequest { TypeName = "Author", Payload = payload };

        var act = async () => await _sut.Update(request, TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.Status.StatusCode.Should().Be(StatusCode.PermissionDenied);
        ex.Which.Status.Detail.Should().Contain("immutable");
    }

    [Fact]
    public async Task Post_PayloadJson_ContainsNativeTypes_NotStringified()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        EntityEvent? captured = null;
        _events.When(e => e.ProduceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EntityEvent>()))
               .Do(call => captured = call.ArgAt<EntityEvent>(2));

        var payload = MakePayload(new()
        {
            ["Name"]        = Value.ForString("Alice"),
            ["IsPublished"] = Value.ForBool(true)
        });
        await _sut.Post(new PersistRequest { TypeName = "Author", Payload = payload }, TestServerCallContext.Create());

        captured.Should().NotBeNull();
        using var doc = JsonDocument.Parse(captured!.PayloadJson);
        doc.RootElement.TryGetProperty("IsPublished", out var prop).Should().BeTrue();
        prop.ValueKind.Should().Be(JsonValueKind.True);
    }
}
