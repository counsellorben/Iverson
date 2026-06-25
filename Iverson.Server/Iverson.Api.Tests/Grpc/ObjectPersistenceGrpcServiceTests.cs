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
        _events.ProduceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EntityEvent>())
               .Returns(Task.CompletedTask);

        _sql = Substitute.For<IPostgresRepository>();
        _sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);

        _registry = new SchemaRegistry(_sql, NullLogger<SchemaRegistry>.Instance);
        _sut = new ObjectPersistenceGrpcService(_events, _registry, NullLogger<ObjectPersistenceGrpcService>.Instance);
    }

    private static Struct MakePayload(Dictionary<string, Value> fields)
    {
        var s = new Struct();
        foreach (var (k, v) in fields) s.Fields[k] = v;
        return s;
    }

    private EntityEvent CaptureEntityEvent()
    {
        EntityEvent? captured = null;
        _events.ProduceAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Do<EntityEvent>(e => captured = e))
            .Returns(Task.CompletedTask);
        return captured!; // will be populated after the call
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
    public async Task Post_UsesClientProvidedKey_WhenPresent()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        var knownGuid = Guid.NewGuid().ToString();
        var payload = MakePayload(new()
        {
            ["Id"]   = Value.ForString(knownGuid),
            ["Name"] = Value.ForString("Bob")
        });
        var request = new PersistRequest { TypeName = "Author", Payload = payload };

        var response = await _sut.Post(request, TestServerCallContext.Create());

        response.Key.Should().Be(knownGuid);
    }

    [Fact]
    public async Task Post_AlwaysIncludesRecord_InTargetStores()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        EntityEvent? captured = null;
        _events.ProduceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Do<EntityEvent>(e => captured = e))
               .Returns(Task.CompletedTask);

        var payload = MakePayload(new() { ["Name"] = Value.ForString("Alice") });
        await _sut.Post(new PersistRequest { TypeName = "Author", Payload = payload }, TestServerCallContext.Create());

        captured.Should().NotBeNull();
        captured!.TargetStores.HasFlag(StoreTarget.Record).Should().BeTrue();
    }

    [Fact]
    public async Task Post_IncludesEngagement_WhenSchemaIsEngagementEligible()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());

        EntityEvent? captured = null;
        _events.ProduceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Do<EntityEvent>(e => captured = e))
               .Returns(Task.CompletedTask);

        var payload = MakePayload(new() { ["Name"] = Value.ForString("Alice") });
        await _sut.Post(new PersistRequest { TypeName = "Author", Payload = payload }, TestServerCallContext.Create());

        captured!.TargetStores.HasFlag(StoreTarget.Engagement).Should().BeTrue();
    }

    [Fact]
    public async Task Post_ExcludesEngagement_WhenSchemaHasOneToMany()
    {
        await _registry.RegisterAsync(SchemaFixtures.ArticleWithOneToManySchema());

        EntityEvent? captured = null;
        _events.ProduceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Do<EntityEvent>(e => captured = e))
               .Returns(Task.CompletedTask);

        // Provide the required AuthorId FK so validation passes
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
        _events.ProduceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Do<EntityEvent>(e => captured = e))
               .Returns(Task.CompletedTask);

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
        _events.ProduceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Do<EntityEvent>(e => captured = e))
               .Returns(Task.CompletedTask);

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
    public async Task Update_ThrowsRpcException_WhenKeyMissing()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        // No Id in payload
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
        _events.ProduceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Do<EntityEvent>(e => captured = e))
               .Returns(Task.CompletedTask);

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
