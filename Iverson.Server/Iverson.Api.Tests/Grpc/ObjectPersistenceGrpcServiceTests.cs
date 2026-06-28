using System.Text.Json;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Grpc;
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
    private readonly IPostgresRepository _sql;
    private readonly SchemaRegistry _registry;
    private readonly ObjectPersistenceGrpcService _sut;

    public ObjectPersistenceGrpcServiceTests()
    {
        _events = Substitute.For<IEventProducer>();

        _sql = Substitute.For<IPostgresRepository>();
        _sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);

        _registry = new SchemaRegistry(_sql, NullLogger<SchemaRegistry>.Instance);
        _sut = new ObjectPersistenceGrpcService(_events, _sql, _registry, NullLogger<ObjectPersistenceGrpcService>.Instance);
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
        _events.When(e => e.PublishFireAndForget(topic, Arg.Any<string>(), Arg.Any<EntityEvent>()))
               .Do(call => captured = call.ArgAt<EntityEvent>(2));
        return captured; // populated after sut call — caller must read after invoking sut
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
        var payload = MakePayload(new() { ["Name"] = Value.ForString("Alice") });

        await _sut.Post(new PersistRequest { TypeName = "Author", Payload = payload }, TestServerCallContext.Create());

        await _sql.Received(1).ExecuteAsync(
            Arg.Is<string>(s => s.Contains("json_populate_record")),
            Arg.Any<object?>());
    }

    [Fact]
    public async Task Post_PublishesFireAndForget_WithEngagementTarget()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        EntityEvent? captured = null;
        _events.When(e => e.PublishFireAndForget(EntityTopics.Created, Arg.Any<string>(), Arg.Any<EntityEvent>()))
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
        _events.When(e => e.PublishFireAndForget(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EntityEvent>()))
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
        _events.When(e => e.PublishFireAndForget(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EntityEvent>()))
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
        _events.When(e => e.PublishFireAndForget(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EntityEvent>()))
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
        _events.When(e => e.PublishFireAndForget(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EntityEvent>()))
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
        var payload = MakePayload(new()
        {
            ["Id"]   = Value.ForString(Guid.NewGuid().ToString()),
            ["Name"] = Value.ForString("Alice")
        });

        await _sut.Update(new PersistRequest { TypeName = "Author", Payload = payload }, TestServerCallContext.Create());

        await _sql.Received(1).ExecuteAsync(
            Arg.Is<string>(s => s.Contains("json_populate_record")),
            Arg.Any<object?>());
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
        _events.When(e => e.PublishFireAndForget(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EntityEvent>()))
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
