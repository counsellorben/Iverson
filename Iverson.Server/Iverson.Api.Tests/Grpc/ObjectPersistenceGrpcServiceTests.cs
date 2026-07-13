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
