using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Authorization;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Iverson.Sql;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

public class ObjectRetrievalGrpcServiceTests
{
    private readonly IRecordStoreQueryExecutor _sql;
    private readonly IEntityRepository _entities;
    private readonly SchemaRegistry _registry;
    private readonly IActingUserAccessor _actingUserAccessor;
    private readonly IRowFieldAuthorizationEvaluator _authEvaluator = new RowFieldAuthorizationEvaluator();
    private readonly ILogger<AuditLog> _auditLogger = Substitute.For<ILogger<AuditLog>>();
    private readonly AuditLog _auditLog;
    private readonly ObjectRetrievalGrpcService _sut;

    private static readonly string AuthorId   = "11111111-0000-0000-0000-000000000001";
    private static readonly string AuthorId2  = "11111111-0000-0000-0000-000000000002";
    private static readonly string AuthorJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","Bio":"Writer","TenantId":"test-tenant"}""";

    public ObjectRetrievalGrpcServiceTests()
    {
        _sql = Substitute.For<IRecordStoreQueryExecutor>();
        _sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);
        _entities = Substitute.For<IEntityRepository>();

        _registry = new SchemaRegistry(new SchemaRegistryRepository(_sql), NullLogger<SchemaRegistry>.Instance);
        _actingUserAccessor = new ActingUserAccessor
            { ActingUser = ActingUserFixtures.Principal("test-user", "test-bypass") };
        _auditLog = new AuditLog(_auditLogger);
        _sut = new ObjectRetrievalGrpcService(_entities, _registry,
            NullLogger<ObjectRetrievalGrpcService>.Instance,
            _actingUserAccessor, _authEvaluator, _auditLog);
    }

    // ── Get ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_WhenEntityExists_ReturnsFoundWithParsedData()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(AuthorJson);

        var response = await _sut.Get(
            new RetrievalRequest { TypeName = "Author", Key = AuthorId, TraceId = "t1" },
            TestServerCallContext.Create());

        response.Found.Should().BeTrue();
        response.TraceId.Should().Be("t1");
        response.Data.Fields["Name"].StringValue.Should().Be("Alice");
    }

    [Fact]
    public async Task Get_WhenEntityNotFound_ReturnsNotFound()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns((string?)null);

        var response = await _sut.Get(
            new RetrievalRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        response.Found.Should().BeFalse();
        response.Data.Should().BeNull();
    }

    [Fact]
    public async Task Get_WhenSchemaNotRegistered_ReturnsNotFound()
    {
        var response = await _sut.Get(
            new RetrievalRequest { TypeName = "Ghost", Key = AuthorId },
            TestServerCallContext.Create());

        response.Found.Should().BeFalse();
        await _entities.DidNotReceive().FetchByKeyAsync(
            Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Get_QueriesCorrectTable_UsingKeyColumnAndTypeName()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(AuthorJson);

        await _sut.Get(
            new RetrievalRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        await _entities.Received(1).FetchByKeyAsync(
            Arg.Is<TableSchema>(s => s.TableName == "authors" && s.KeyColumn.Name == "Id"),
            AuthorId, Arg.Any<bool>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Get_PreservesTraceId_InResponse()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns((string?)null);

        var response = await _sut.Get(
            new RetrievalRequest { TypeName = "Author", Key = AuthorId, TraceId = "trace-xyz" },
            TestServerCallContext.Create());

        response.TraceId.Should().Be("trace-xyz");
    }

    // ── GetMany ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetMany_StreamsFoundResponseForEachKey()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _entities.FetchManyByKeysAsync(Arg.Any<TableSchema>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(new[] { new KeyedRow(AuthorId, AuthorJson), new KeyedRow(AuthorId2, AuthorJson) });

        var stream = MakeStream<RetrievalResponse>();
        await _sut.GetMany(
            new RetrievalManyRequest { TypeName = "Author", Keys = { AuthorId, AuthorId2 } },
            stream, TestServerCallContext.Create());

        stream.Written.Should().HaveCount(2);
        stream.Written.Should().AllSatisfy(r => r.Found.Should().BeTrue());
    }

    [Fact]
    public async Task GetMany_WhenEntityMissing_StreamsNotFoundForThatKey()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _entities.FetchManyByKeysAsync(Arg.Any<TableSchema>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(new[] { new KeyedRow(AuthorId, AuthorJson) }); // AuthorId2 absent

        var stream = MakeStream<RetrievalResponse>();
        await _sut.GetMany(
            new RetrievalManyRequest { TypeName = "Author", Keys = { AuthorId, AuthorId2 } },
            stream, TestServerCallContext.Create());

        stream.Written[0].Found.Should().BeTrue();
        stream.Written[1].Found.Should().BeFalse();
    }

    [Fact]
    public async Task GetMany_WhenSchemaNotRegistered_StreamsNotFoundForAllKeys()
    {
        var stream = MakeStream<RetrievalResponse>();
        await _sut.GetMany(
            new RetrievalManyRequest { TypeName = "Ghost", Keys = { AuthorId, AuthorId2 } },
            stream, TestServerCallContext.Create());

        stream.Written.Should().HaveCount(2);
        stream.Written.Should().AllSatisfy(r => r.Found.Should().BeFalse());
        await _entities.DidNotReceive().FetchManyByKeysAsync(
            Arg.Any<TableSchema>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<bool>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task GetMany_PreservesTraceId_InEachResponse()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _entities.FetchManyByKeysAsync(Arg.Any<TableSchema>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(new[] { new KeyedRow(AuthorId, AuthorJson) });

        var stream = MakeStream<RetrievalResponse>();
        await _sut.GetMany(
            new RetrievalManyRequest { TypeName = "Author", Keys = { AuthorId }, TraceId = "trace-abc" },
            stream, TestServerCallContext.Create());

        stream.Written[0].TraceId.Should().Be("trace-abc");
    }

    [Fact]
    public async Task GetMany_IssuesSingleBatchQuery_RegardlessOfKeyCount()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _entities.FetchManyByKeysAsync(Arg.Any<TableSchema>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(Array.Empty<KeyedRow>());

        var stream = MakeStream<RetrievalResponse>();
        await _sut.GetMany(
            new RetrievalManyRequest { TypeName = "Author", Keys = { AuthorId, AuthorId2 } },
            stream, TestServerCallContext.Create());

        await _entities.Received(1).FetchManyByKeysAsync(
            Arg.Any<TableSchema>(),
            Arg.Is<IReadOnlyList<string>>(keys => keys.Count == 2), Arg.Any<bool>(), Arg.Any<string?>());
    }

    // ── authorization fixtures ───────────────────────────────────────────────

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

    // ── Get authorization ────────────────────────────────────────────────────

    [Fact]
    public async Task Get_WithNoAuthorizationRulesConfigured_ReturnsNotFound()
    {
        var schema = SchemaFixtures.AuthorSchema() with { Authorization = null };
        await _registry.RegisterAsync(schema);
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(AuthorJson);

        var response = await _sut.Get(
            new RetrievalRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        response.Found.Should().BeFalse();
        response.Data.Should().BeNull();
    }

    [Fact]
    public async Task Get_WithNoActingUser_ReturnsNotFound()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(AuthorJson);
        _actingUserAccessor.ActingUser = null;

        var response = await _sut.Get(
            new RetrievalRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        response.Found.Should().BeFalse();
        response.Data.Should().BeNull();
    }

    [Fact]
    public async Task Get_WithMatchingOwner_ReturnsFound()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema());
        var ownedJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","OwnerId":"test-user","TenantId":"test-tenant"}""";
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(ownedJson);

        var response = await _sut.Get(
            new RetrievalRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        response.Found.Should().BeTrue();
        response.Data.Fields["Name"].StringValue.Should().Be("Alice");
    }

    [Fact]
    public async Task Get_WithBypassRole_ReturnsFound_EvenWhenNotOwner()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema(withBypassRole: true));
        var ownedJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","OwnerId":"someone-else","TenantId":"test-tenant"}""";
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(ownedJson);

        var response = await _sut.Get(
            new RetrievalRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        response.Found.Should().BeTrue();
    }

    [Fact]
    public async Task Get_WithNonMatchingOwner_ReturnsNotFound()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema());
        var ownedJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","OwnerId":"someone-else","TenantId":"test-tenant"}""";
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(ownedJson);

        var response = await _sut.Get(
            new RetrievalRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        response.Found.Should().BeFalse();
        response.Data.Should().BeNull();
    }

    [Fact]
    public async Task Get_WithNonMatchingTenant_ReturnsNotFound()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        var crossTenantJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","Bio":"Writer","TenantId":"other-tenant"}""";
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(crossTenantJson);

        var response = await _sut.Get(
            new RetrievalRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        response.Found.Should().BeFalse();
        response.Data.Should().BeNull();
    }

    [Fact]
    public async Task Get_WithBypassRoleAndNonMatchingTenant_ReturnsNotFound()
    {
        // Tenant is strictly additive: a CanReadAll bypass role must not exempt the caller
        // from the tenant boundary.
        await _registry.RegisterAsync(OwnedAuthorSchema(withBypassRole: true));
        var crossTenantJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","OwnerId":"someone-else","TenantId":"other-tenant"}""";
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(crossTenantJson);

        var response = await _sut.Get(
            new RetrievalRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        response.Found.Should().BeFalse();
        response.Data.Should().BeNull();
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
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(AuthorJson);

        var response = await _sut.Get(
            new RetrievalRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        response.Found.Should().BeTrue();
        response.Data.Fields.Should().ContainKey("Name");
        response.Data.Fields.Should().NotContainKey("Bio");
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
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(AuthorJson);

        await _sut.Get(
            new RetrievalRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        AssertAuditLogged("AccessDenied");
    }

    [Fact]
    public async Task Get_OwnerMismatch_LogsAuditDeniedWithOwnerMismatch()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema());
        var ownedJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","OwnerId":"someone-else","TenantId":"test-tenant"}""";
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(ownedJson);

        await _sut.Get(
            new RetrievalRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        AssertAuditLogged("OwnerMismatch");
    }

    [Fact]
    public async Task Get_TenantMismatch_LogsAuditDeniedWithTenantMismatch()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        var crossTenantJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","Bio":"Writer","TenantId":"other-tenant"}""";
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(crossTenantJson);

        await _sut.Get(
            new RetrievalRequest { TypeName = "Author", Key = AuthorId },
            TestServerCallContext.Create());

        AssertAuditLogged("TenantMismatch");
    }

    // ── GetMany authorization ────────────────────────────────────────────────

    [Fact]
    public async Task GetMany_WithNoAuthorizationRulesConfigured_StreamsNotFoundForAllKeys()
    {
        var schema = SchemaFixtures.AuthorSchema() with { Authorization = null };
        await _registry.RegisterAsync(schema);
        _entities.FetchManyByKeysAsync(Arg.Any<TableSchema>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(new[] { new KeyedRow(AuthorId, AuthorJson), new KeyedRow(AuthorId2, AuthorJson) });

        var stream = MakeStream<RetrievalResponse>();
        await _sut.GetMany(
            new RetrievalManyRequest { TypeName = "Author", Keys = { AuthorId, AuthorId2 } },
            stream, TestServerCallContext.Create());

        stream.Written.Should().HaveCount(2);
        stream.Written.Should().AllSatisfy(r => r.Found.Should().BeFalse());
        await _entities.DidNotReceive().FetchManyByKeysAsync(
            Arg.Any<TableSchema>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<bool>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task GetMany_WithNoActingUser_StreamsNotFoundForAllKeys()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        _entities.FetchManyByKeysAsync(Arg.Any<TableSchema>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(new[] { new KeyedRow(AuthorId, AuthorJson), new KeyedRow(AuthorId2, AuthorJson) });
        _actingUserAccessor.ActingUser = null;

        var stream = MakeStream<RetrievalResponse>();
        await _sut.GetMany(
            new RetrievalManyRequest { TypeName = "Author", Keys = { AuthorId, AuthorId2 } },
            stream, TestServerCallContext.Create());

        stream.Written.Should().HaveCount(2);
        stream.Written.Should().AllSatisfy(r => r.Found.Should().BeFalse());
        await _entities.DidNotReceive().FetchManyByKeysAsync(
            Arg.Any<TableSchema>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<bool>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task GetMany_WithMatchingOwner_StreamsFound()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema());
        var ownedJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","OwnerId":"test-user","TenantId":"test-tenant"}""";
        _entities.FetchManyByKeysAsync(Arg.Any<TableSchema>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(new[] { new KeyedRow(AuthorId, ownedJson) });

        var stream = MakeStream<RetrievalResponse>();
        await _sut.GetMany(
            new RetrievalManyRequest { TypeName = "Author", Keys = { AuthorId } },
            stream, TestServerCallContext.Create());

        stream.Written.Should().HaveCount(1);
        stream.Written[0].Found.Should().BeTrue();
        stream.Written[0].Data.Fields["Name"].StringValue.Should().Be("Alice");
    }

    [Fact]
    public async Task GetMany_WithBypassRole_StreamsFound_EvenWhenNotOwner()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema(withBypassRole: true));
        var ownedJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","OwnerId":"someone-else","TenantId":"test-tenant"}""";
        _entities.FetchManyByKeysAsync(Arg.Any<TableSchema>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(new[] { new KeyedRow(AuthorId, ownedJson) });

        var stream = MakeStream<RetrievalResponse>();
        await _sut.GetMany(
            new RetrievalManyRequest { TypeName = "Author", Keys = { AuthorId } },
            stream, TestServerCallContext.Create());

        stream.Written.Should().HaveCount(1);
        stream.Written[0].Found.Should().BeTrue();
    }

    [Fact]
    public async Task GetMany_WithNonMatchingOwner_StreamsNotFound()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema());
        var ownedJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","OwnerId":"someone-else","TenantId":"test-tenant"}""";
        _entities.FetchManyByKeysAsync(Arg.Any<TableSchema>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(new[] { new KeyedRow(AuthorId, ownedJson) });

        var stream = MakeStream<RetrievalResponse>();
        await _sut.GetMany(
            new RetrievalManyRequest { TypeName = "Author", Keys = { AuthorId } },
            stream, TestServerCallContext.Create());

        stream.Written.Should().HaveCount(1);
        stream.Written[0].Found.Should().BeFalse();
    }

    [Fact]
    public async Task GetMany_WithNonMatchingTenant_StreamsNotFound()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        var crossTenantJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","Bio":"Writer","TenantId":"other-tenant"}""";
        _entities.FetchManyByKeysAsync(Arg.Any<TableSchema>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(new[] { new KeyedRow(AuthorId, crossTenantJson) });

        var stream = MakeStream<RetrievalResponse>();
        await _sut.GetMany(
            new RetrievalManyRequest { TypeName = "Author", Keys = { AuthorId } },
            stream, TestServerCallContext.Create());

        stream.Written.Should().HaveCount(1);
        stream.Written[0].Found.Should().BeFalse();
    }

    [Fact]
    public async Task GetMany_WithBypassRoleAndNonMatchingTenant_StreamsNotFound()
    {
        // Tenant is strictly additive: a CanReadAll bypass role must not exempt the caller
        // from the tenant boundary.
        await _registry.RegisterAsync(OwnedAuthorSchema(withBypassRole: true));
        var crossTenantJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","OwnerId":"someone-else","TenantId":"other-tenant"}""";
        _entities.FetchManyByKeysAsync(Arg.Any<TableSchema>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(new[] { new KeyedRow(AuthorId, crossTenantJson) });

        var stream = MakeStream<RetrievalResponse>();
        await _sut.GetMany(
            new RetrievalManyRequest { TypeName = "Author", Keys = { AuthorId } },
            stream, TestServerCallContext.Create());

        stream.Written.Should().HaveCount(1);
        stream.Written[0].Found.Should().BeFalse();
    }

    [Fact]
    public async Task GetMany_WithRestrictedField_OmitsFieldFromResponse()
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
        _entities.FetchManyByKeysAsync(Arg.Any<TableSchema>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(new[] { new KeyedRow(AuthorId, AuthorJson) });

        var stream = MakeStream<RetrievalResponse>();
        await _sut.GetMany(
            new RetrievalManyRequest { TypeName = "Author", Keys = { AuthorId } },
            stream, TestServerCallContext.Create());

        stream.Written.Should().HaveCount(1);
        stream.Written[0].Found.Should().BeTrue();
        stream.Written[0].Data.Fields.Should().ContainKey("Name");
        stream.Written[0].Data.Fields.Should().NotContainKey("Bio");
    }

    // ── GetMany audit logging ────────────────────────────────────────────────

    [Fact]
    public async Task GetMany_AccessDenied_LogsAuditDeniedOnce()
    {
        var schema = SchemaFixtures.AuthorSchema() with { Authorization = null };
        await _registry.RegisterAsync(schema);
        _entities.FetchManyByKeysAsync(Arg.Any<TableSchema>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(new[] { new KeyedRow(AuthorId, AuthorJson), new KeyedRow(AuthorId2, AuthorJson) });

        var stream = MakeStream<RetrievalResponse>();
        await _sut.GetMany(
            new RetrievalManyRequest { TypeName = "Author", Keys = { AuthorId, AuthorId2 } },
            stream, TestServerCallContext.Create());

        AssertAuditLogged("AccessDenied");
    }

    [Fact]
    public async Task GetMany_OwnerMismatch_LogsAuditDeniedWithOwnerMismatch()
    {
        await _registry.RegisterAsync(OwnedAuthorSchema());
        var ownedJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","OwnerId":"someone-else","TenantId":"test-tenant"}""";
        _entities.FetchManyByKeysAsync(Arg.Any<TableSchema>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(new[] { new KeyedRow(AuthorId, ownedJson) });

        var stream = MakeStream<RetrievalResponse>();
        await _sut.GetMany(
            new RetrievalManyRequest { TypeName = "Author", Keys = { AuthorId } },
            stream, TestServerCallContext.Create());

        AssertAuditLogged("OwnerMismatch");
    }

    [Fact]
    public async Task GetMany_TenantMismatch_LogsAuditDeniedWithTenantMismatch()
    {
        await _registry.RegisterAsync(SchemaFixtures.AuthorSchema());
        var crossTenantJson = $$"""{"Id":"{{AuthorId}}","Name":"Alice","Bio":"Writer","TenantId":"other-tenant"}""";
        _entities.FetchManyByKeysAsync(Arg.Any<TableSchema>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(new[] { new KeyedRow(AuthorId, crossTenantJson) });

        var stream = MakeStream<RetrievalResponse>();
        await _sut.GetMany(
            new RetrievalManyRequest { TypeName = "Author", Keys = { AuthorId } },
            stream, TestServerCallContext.Create());

        AssertAuditLogged("TenantMismatch");
    }

    // ── stream helper ────────────────────────────────────────────────────────

    private static FakeStream<T> MakeStream<T>() => new();

    private sealed class FakeStream<T> : IServerStreamWriter<T>
    {
        public List<T> Written { get; } = [];
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(T message) { Written.Add(message); return Task.CompletedTask; }
        public Task WriteAsync(T message, CancellationToken cancellationToken)
            { Written.Add(message); return Task.CompletedTask; }
    }
}
