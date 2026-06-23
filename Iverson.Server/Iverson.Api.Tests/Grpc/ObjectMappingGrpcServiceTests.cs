using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Iverson.Elasticsearch;
using Iverson.Events;
using Iverson.Sql;
using Iverson.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

public class ObjectMappingGrpcServiceTests
{
    private readonly IPostgresRepository _sql;
    private readonly IElasticsearchService _es;
    private readonly IVectorService _vector;
    private readonly IEventProducer _events;
    private readonly SchemaRegistry _registry;
    private readonly ObjectMappingGrpcService _sut;

    private static readonly string AuthorId  = "11111111-0000-0000-0000-000000000001";
    private static readonly string ArticleId = "22222222-0000-0000-0000-000000000002";
    private static readonly string AuthorJson  = $$"""{"Id":"{{AuthorId}}","Name":"Alice","Bio":"Writer"}""";
    private static readonly string ArticleJson = $$"""{"Id":"{{ArticleId}}","Title":"Hello","Body":"World","AuthorId":"{{AuthorId}}"}""";

    public ObjectMappingGrpcServiceTests()
    {
        _sql    = Substitute.For<IPostgresRepository>();
        _es     = Substitute.For<IElasticsearchService>();
        _vector = Substitute.For<IVectorService>();
        _events = Substitute.For<IEventProducer>();

        _sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(1);
        _sql.QueryAsync<string>(Arg.Any<string>(), Arg.Any<object?>())
            .Returns(Task.FromResult(Enumerable.Empty<string>()));
        _events.ProduceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EntityEvent>())
               .Returns(Task.CompletedTask);

        _registry = new SchemaRegistry(_sql, NullLogger<SchemaRegistry>.Instance);
        _sut = new ObjectMappingGrpcService(
            _sql, _es, _vector, _events, _registry,
            NullLogger<ObjectMappingGrpcService>.Instance);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static Struct MakePayload(Dictionary<string, Value> fields)
    {
        var s = new Struct();
        foreach (var (k, v) in fields) s.Fields[k] = v;
        return s;
    }

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
        _events.ProduceAsync(
            topic,
            Arg.Any<string>(),
            Arg.Do<EntityEvent>(e => captured = e))
            .Returns(Task.CompletedTask);
        return captured; // populated after sut call
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
        td.Relations.Add(new Iverson.Client.Contracts.RelationDescriptor
        {
            PropertyName = "Article",
            Kind         = Iverson.Client.Contracts.RelationKind.ManyToOne,
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
        td.Relations.Add(new Iverson.Client.Contracts.RelationDescriptor
        {
            PropertyName = "Tags",
            Kind         = Iverson.Client.Contracts.RelationKind.ManyToMany,
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

    // ── Post ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_WithMissingKey_GeneratesValidGuid()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _sql.QuerySingleOrDefaultAsync<string>(Arg.Any<string>(), Arg.Any<object?>())
            .Returns(AuthorJson);

        EntityEvent? evt = null;
        _events.ProduceAsync(EntityTopics.Created, Arg.Any<string>(), Arg.Do<EntityEvent>(e => evt = e))
               .Returns(Task.CompletedTask);

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
        _events.ProduceAsync(EntityTopics.Created, Arg.Any<string>(), Arg.Do<EntityEvent>(e => evt = e))
               .Returns(Task.CompletedTask);

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
    public async Task Post_ExecutesUpsertSqlWithJsonPopulateRecord()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _sql.QuerySingleOrDefaultAsync<string>(Arg.Any<string>(), Arg.Any<object?>())
            .Returns(AuthorJson);

        var payload = MakePayload(new()
        {
            ["Id"]   = Value.ForString(AuthorId),
            ["Name"] = Value.ForString("Alice")
        });
        await _sut.Post(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        await _sql.Received().ExecuteAsync(
            Arg.Is<string>(s => s.Contains("json_populate_record")),
            Arg.Any<object?>());
    }

    [Fact]
    public async Task Post_EmitsCreatedEvent_WithCorrectTypeName()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _sql.QuerySingleOrDefaultAsync<string>(Arg.Any<string>(), Arg.Any<object?>())
            .Returns(AuthorJson);

        EntityEvent? evt = null;
        _events.ProduceAsync(EntityTopics.Created, Arg.Any<string>(), Arg.Do<EntityEvent>(e => evt = e))
               .Returns(Task.CompletedTask);

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
        _sql.QuerySingleOrDefaultAsync<string>(Arg.Any<string>(), Arg.Any<object?>())
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
        _sql.QuerySingleOrDefaultAsync<string>(Arg.Any<string>(), Arg.Any<object?>())
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

        _sql.QuerySingleOrDefaultAsync<string>(
                Arg.Is<string>(s => s.Contains("\"articles\"")), Arg.Any<object?>())
            .Returns(ArticleJson);
        _sql.QuerySingleOrDefaultAsync<string>(
                Arg.Is<string>(s => s.Contains("\"authors\"")), Arg.Any<object?>())
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

        _sql.QuerySingleOrDefaultAsync<string>(Arg.Any<string>(), Arg.Any<object?>())
            .Returns(ArticleJson);

        var response = await _sut.Get(
            new MappingGetRequest { TypeName = "Article", Key = ArticleId, Depth = 0 },
            TestServerCallContext.Create());

        response.Success.Should().BeTrue();
        response.Data.Fields.Should().NotContainKey("Author");
        // SQL called exactly once (for the article itself)
        await _sql.Received(1).QuerySingleOrDefaultAsync<string>(Arg.Any<string>(), Arg.Any<object?>());
    }

    // ── Update ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_WithValidKey_UpsertsSqlAndEmitsUpdatedEvent()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _sql.QuerySingleOrDefaultAsync<string>(Arg.Any<string>(), Arg.Any<object?>())
            .Returns(AuthorJson);

        EntityEvent? evt = null;
        _events.ProduceAsync(EntityTopics.Updated, Arg.Any<string>(), Arg.Do<EntityEvent>(e => evt = e))
               .Returns(Task.CompletedTask);

        var payload = MakePayload(new()
        {
            ["Id"]   = Value.ForString(AuthorId),
            ["Name"] = Value.ForString("Alice Updated")
        });
        var response = await _sut.Update(
            new MappingWriteRequest { TypeName = "Author", Payload = payload },
            TestServerCallContext.Create());

        response.Success.Should().BeTrue();
        await _sql.Received().ExecuteAsync(
            Arg.Is<string>(s => s.Contains("json_populate_record")),
            Arg.Any<object?>());
        evt!.Key.Should().Be(AuthorId);
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
        _sql.QuerySingleOrDefaultAsync<string>(Arg.Any<string>(), Arg.Any<object?>())
            .Returns(AuthorJson);

        EntityEvent? evt = null;
        _events.ProduceAsync(EntityTopics.Deleted, Arg.Any<string>(), Arg.Do<EntityEvent>(e => evt = e))
               .Returns(Task.CompletedTask);

        var response = await _sut.Delete(
            new MappingDeleteRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        response.Success.Should().BeTrue();
        await _sql.Received().ExecuteAsync(
            Arg.Is<string>(s => s.Contains("DELETE FROM")),
            Arg.Any<object?>());
        evt!.TypeName.Should().Be("Author");
        evt.Key.Should().Be(AuthorId);
    }

    [Fact]
    public async Task Delete_WhenEntityNotFound_ReturnsFailureWithoutEmittingEvent()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _sql.QuerySingleOrDefaultAsync<string>(Arg.Any<string>(), Arg.Any<object?>())
            .Returns((string?)null);

        var response = await _sut.Delete(
            new MappingDeleteRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        response.Success.Should().BeFalse();
        response.Error.Should().Contain("not found");
        await _events.DidNotReceive().ProduceAsync(
            EntityTopics.Deleted, Arg.Any<string>(), Arg.Any<EntityEvent>());
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
