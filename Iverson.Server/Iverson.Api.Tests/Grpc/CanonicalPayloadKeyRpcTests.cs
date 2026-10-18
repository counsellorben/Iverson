using System.Text.Json;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Authorization;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Iverson.Sql;
using Iverson.StarRocks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using SchemaAuthorizationRules = Iverson.Api.Schema.AuthorizationRules;
using SchemaRowPermission      = Iverson.Api.Schema.RowPermission;

namespace Iverson.Api.Tests.Grpc;

/// <summary>
/// CSR round 12 #2. The four client write RPCs rename each payload key to the spelling of the
/// column it names, ignoring case, before anything reads a key by name.
/// <para>
/// Before this, a re-cased owner key slipped past the owner checks, which look a name up exactly
/// or lower-first, and StarRocks then wrote it into the owner column. And the .NET client's
/// camelCase policy turns <c>URLPath</c> into <c>urlPath</c>, which the server upper-firsted to
/// <c>UrlPath</c>, so Postgres' case-sensitive <c>json_populate_record</c> stored NULL. Each test
/// reads the JSON the RPC hands to the Postgres upsert.
/// </para>
/// </summary>
public class CanonicalPayloadKeyRpcTests
{
    private const string LinkKey    = "55555555-0000-0000-0000-000000000005";
    private const string DossierKey = "66666666-0000-0000-0000-000000000006";

    private readonly SchemaRegistry _registry =
        new(Substitute.For<ISchemaRegistryRepository>(), NullLogger<SchemaRegistry>.Instance);
    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();
    private readonly IOutboxWriter _outboxWriter = Substitute.For<IOutboxWriter>();
    private readonly List<string> _written = [];
    private readonly ObjectPersistenceGrpcService _persistence;
    private readonly ObjectMappingGrpcService _mapping;

    public CanonicalPayloadKeyRpcTests()
    {
        var actingUser = new ActingUserAccessor { ActingUser = ActingUserFixtures.Principal("test-user", "test-bypass") };
        var authEvaluator = new RowFieldAuthorizationEvaluator();
        var outboxPublisher = Substitute.For<IOutboxPublisher>();
        var auditLog = new AuditLog(NullLogger<AuditLog>.Instance);

        // NSubstitute's auto-value for Task<string?> is "", not null. Update answers NotFound when
        // the tenant-scoped pre-fetch finds nothing, so update tests stub the stored row.
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<EntityAccess>())
            .Returns((string?)null);

        // The JSON each write hands to the Postgres upsert (json_populate_record), which the outbox
        // event also carries to the projections.
        _outboxWriter
            .UpsertAndEnqueueOutboxAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Do<string>(json => _written.Add(json)), Arg.Any<string?>())
            .Returns(Guid.NewGuid());

        _persistence = new ObjectPersistenceGrpcService(
            outboxPublisher,
            _registry,
            new RelationValidator(),
            new PayloadSizeValidator(),
            new EntityKeyAccessor(),
            _outboxWriter,
            NullLogger<ObjectPersistenceGrpcService>.Instance,
            _entities,
            actingUser,
            authEvaluator,
            auditLog);
        _mapping = new ObjectMappingGrpcService(
            _entities,
            Substitute.For<IRecordStoreTransactionRunner>(),
            outboxPublisher,
            _registry,
            new RelationValidator(),
            new PayloadSizeValidator(),
            new EntityKeyAccessor(),
            _outboxWriter,
            NullLogger<ObjectMappingGrpcService>.Instance,
            actingUser,
            authEvaluator,
            Substitute.For<IEntityRelationResolver>(),
            Substitute.For<ISchemaRegistrationOrchestrator>(),
            auditLog,
            EngagementQueryLimitOptions.Default);
    }

    // ID and URLPath start with an acronym, which the .NET client's camelCase policy folds: it
    // sends "id" and "urlPath". url starts lower-case, which a property name may.
    private static SchemaDescriptor LinkSchema() => new()
    {
        TypeName      = "Link",
        TableName     = "links",
        KeyColumn     = new ColumnDescriptor("ID", "UUID", false),
        ScalarColumns =
        [
            new ColumnDescriptor("URLPath", "TEXT", true),
            new ColumnDescriptor("url",     "TEXT", true),
            new ColumnDescriptor(SchemaDescriptor.TenantColumnName, "TEXT", false),
        ],
        FkColumns     = [],
        VectorFields  = [],
        ChunkFields   = [],
        Relations     = [],
        Authorization = new SchemaAuthorizationRules(
            null, [new SchemaRowPermission("test-bypass", true, true, true)], []),
        TenantColumn  = SchemaDescriptor.TenantColumnName
    };

    // A type stored before case-duplicate columns were refused at registration (user decision 7).
    private static SchemaDescriptor CaseDuplicateSchema() => LinkSchema() with
    {
        TypeName      = "LegacyLink",
        TableName     = "legacy_links",
        ScalarColumns =
        [
            new ColumnDescriptor("Url", "TEXT", true),
            new ColumnDescriptor("URL", "TEXT", true),
            new ColumnDescriptor(SchemaDescriptor.TenantColumnName, "TEXT", false),
        ]
    };

    private void StubStoredLink() =>
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<EntityAccess>())
            .Returns($$"""{"ID":"{{LinkKey}}","URLPath":"/old","url":"old","__TenantId":"test-tenant"}""");

    private void StubStoredDossier() =>
        _entities.FetchByKeyAsync(Arg.Any<TableSchema>(), Arg.Any<string>(), Arg.Any<EntityAccess>())
            .Returns($$"""{"Id":"{{DossierKey}}","Title":"old title","OwnerId":"someone-else","__TenantId":"test-tenant"}""");

    private static Struct MakePayload(Dictionary<string, Value> fields)
    {
        var s = new Struct();
        foreach (var (k, v) in fields) s.Fields[k] = v;
        return s;
    }

    private JsonElement WrittenRow() =>
        JsonDocument.Parse(_written.Should().ContainSingle().Which).RootElement.Clone();

    // ── Acronym-led and lower-case-first columns, at each of the four RPCs ────

    [Fact]
    public async Task PersistencePost_AcronymAndLowerCaseFirstKeys_ReachTheUpsertUnderTheColumnsSpelling()
    {
        await _registry.RegisterAsync(LinkSchema());

        await _persistence.Post(new PersistRequest
        {
            TypeName = "Link",
            Payload  = MakePayload(new() { ["urlPath"] = Value.ForString("/a"), ["url"] = Value.ForString("a") })
        }, TestServerCallContext.Create());

        var row = WrittenRow();
        row.GetProperty("URLPath").GetString().Should().Be("/a");
        row.GetProperty("url").GetString().Should().Be("a");
    }

    [Fact]
    public async Task PersistenceUpdate_AcronymKeyAndColumns_ReachTheUpsertUnderTheColumnsSpelling()
    {
        await _registry.RegisterAsync(LinkSchema());
        StubStoredLink();

        var response = await _persistence.Update(new PersistRequest
        {
            TypeName = "Link",
            Payload  = MakePayload(new()
            {
                ["id"]      = Value.ForString(LinkKey),
                ["urlPath"] = Value.ForString("/b"),
                ["url"]     = Value.ForString("b"),
            })
        }, TestServerCallContext.Create());

        response.Key.Should().Be(LinkKey);
        var row = WrittenRow();
        row.GetProperty("ID").GetString().Should().Be(LinkKey);
        row.GetProperty("URLPath").GetString().Should().Be("/b");
        row.GetProperty("url").GetString().Should().Be("b");
    }

    [Fact]
    public async Task MappingPost_AcronymAndLowerCaseFirstKeys_ReachTheUpsertAndTheResponseUnderTheColumnsSpelling()
    {
        await _registry.RegisterAsync(LinkSchema());

        var response = await _mapping.Post(new MappingWriteRequest
        {
            TypeName = "Link",
            Payload  = MakePayload(new() { ["urlPath"] = Value.ForString("/a"), ["url"] = Value.ForString("a") })
        }, TestServerCallContext.Create());

        var row = WrittenRow();
        row.GetProperty("URLPath").GetString().Should().Be("/a");
        row.GetProperty("url").GetString().Should().Be("a");
        // Post echoes the request payload, so the caller now gets the registered spelling back.
        response.Data.Fields.Keys.Should().Contain(["ID", "URLPath", "url"]);
    }

    [Fact]
    public async Task MappingUpdate_AcronymKeyAndColumns_ReachTheUpsertUnderTheColumnsSpelling()
    {
        await _registry.RegisterAsync(LinkSchema());
        StubStoredLink();

        var response = await _mapping.Update(new MappingWriteRequest
        {
            TypeName = "Link",
            Payload  = MakePayload(new()
            {
                ["id"]      = Value.ForString(LinkKey),
                ["urlPath"] = Value.ForString("/b"),
                ["url"]     = Value.ForString("b"),
            })
        }, TestServerCallContext.Create());

        response.Success.Should().BeTrue();
        var row = WrittenRow();
        row.GetProperty("ID").GetString().Should().Be(LinkKey);
        row.GetProperty("URLPath").GetString().Should().Be("/b");
        row.GetProperty("url").GetString().Should().Be("b");
    }

    // ── A re-cased owner key is an owner change ───────────────────────────────
    // "test-bypass" may write every Dossier row (CanWriteAll), so only OwnerImmutable stands between
    // it and reassigning a row it does not own.

    [Fact]
    public async Task PersistenceUpdate_AReCasedOwnerKey_IsRefusedAsAnOwnerChange()
    {
        await _registry.RegisterAsync(SchemaFixtures.ReservedTenantWriteOnlyDossierSchema(withOwnerField: true));
        StubStoredDossier();

        var act = () => _persistence.Update(new PersistRequest
        {
            TypeName = "Dossier",
            Payload  = MakePayload(new()
            {
                ["Id"]      = Value.ForString(DossierKey),
                ["Title"]   = Value.ForString("new title"),
                ["OWNERID"] = Value.ForString("test-user"),
            })
        }, TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        ex.Which.Status.Detail.Should().Contain("immutable");
        _written.Should().BeEmpty();
    }

    [Fact]
    public async Task MappingUpdate_AReCasedOwnerKey_IsRefusedAsAnOwnerChange()
    {
        await _registry.RegisterAsync(SchemaFixtures.ReservedTenantWriteOnlyDossierSchema(withOwnerField: true));
        StubStoredDossier();

        var act = () => _mapping.Update(new MappingWriteRequest
        {
            TypeName = "Dossier",
            Payload  = MakePayload(new()
            {
                ["Id"]      = Value.ForString(DossierKey),
                ["Title"]   = Value.ForString("new title"),
                ["OWNERID"] = Value.ForString("test-user"),
            })
        }, TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        ex.Which.Status.Detail.Should().Contain("immutable");
        _written.Should().BeEmpty();
    }

    // ── Keys that cannot be mapped to one column ──────────────────────────────

    [Fact]
    public async Task Post_TwoKeysNamingOneColumn_IsInvalidArgument()
    {
        await _registry.RegisterAsync(LinkSchema());

        var act = () => _persistence.Post(new PersistRequest
        {
            TypeName = "Link",
            Payload  = MakePayload(new() { ["URLPath"] = Value.ForString("/a"), ["urlPath"] = Value.ForString("/b") })
        }, TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("'URLPath'").And.Contain("'urlPath'");
        _written.Should().BeEmpty();
    }

    [Fact]
    public async Task Post_AKeyMatchingCaseDuplicateColumns_IsInvalidArgument()
    {
        await _registry.RegisterAsync(CaseDuplicateSchema());

        var act = () => _persistence.Post(new PersistRequest
        {
            TypeName = "LegacyLink",
            Payload  = MakePayload(new() { ["url"] = Value.ForString("a") })
        }, TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("'Url'").And.Contain("'URL'");
        _written.Should().BeEmpty();
    }

    // ── What canonicalisation leaves alone ────────────────────────────────────

    [Fact]
    public async Task Post_AReCasedTenantColumn_IsRefusedUnderTheCallersOwnSpelling()
    {
        // The reserved tenant column is not a rename target, so the smuggling check still names
        // what the caller sent.
        await _registry.RegisterAsync(LinkSchema());

        var act = () => _persistence.Post(new PersistRequest
        {
            TypeName = "Link",
            Payload  = MakePayload(new() { ["__tenantid"] = Value.ForString("other-tenant") })
        }, TestServerCallContext.Create());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("'__tenantid'");
    }

    [Fact]
    public async Task Post_KeysThatAreNotColumns_AreStillUpperFirstedAtEveryLevel()
    {
        // Only a top-level key that is exactly a column keeps its spelling. A nested key never
        // names a column, even when it is spelled like one.
        await _registry.RegisterAsync(LinkSchema());
        var meta = new Struct { Fields = { ["url"] = Value.ForString("nested") } };

        await _persistence.Post(new PersistRequest
        {
            TypeName = "Link",
            Payload  = MakePayload(new() { ["extra"] = Value.ForString("x"), ["meta"] = Value.ForStruct(meta) })
        }, TestServerCallContext.Create());

        var row = WrittenRow();
        row.GetProperty("Extra").GetString().Should().Be("x");
        row.GetProperty("Meta").GetProperty("Url").GetString().Should().Be("nested");
    }

    // The canonicalizer's column map and the serializer's column-name set are cached per descriptor
    // instance. A re-registration replaces the instance, so the next write must use the new spelling
    // even when only a column's letter case changed. "Url" then "url": a stale map would rename the
    // key back to "Url", and a stale set would upper-first it to "Url". The type name is this test's
    // own, so a cache wrongly keyed on the name could only hold this test's first registration.
    [Fact]
    public async Task Post_AfterTheTypeIsReRegisteredWithAReCasedColumn_UsesTheNewSpelling()
    {
        var typeName = $"ReRegisteredLink{Guid.NewGuid():N}";
        var upper = LinkSchema() with
        {
            TypeName      = typeName,
            ScalarColumns =
            [
                new ColumnDescriptor("Url", "TEXT", true),
                new ColumnDescriptor(SchemaDescriptor.TenantColumnName, "TEXT", false),
            ]
        };
        var lower = upper with
        {
            ScalarColumns =
            [
                new ColumnDescriptor("url", "TEXT", true),
                new ColumnDescriptor(SchemaDescriptor.TenantColumnName, "TEXT", false),
            ]
        };
        async Task<JsonElement> PostAsync()
        {
            _written.Clear();
            await _persistence.Post(new PersistRequest
            {
                TypeName = typeName,
                Payload  = MakePayload(new() { ["url"] = Value.ForString("a") })
            }, TestServerCallContext.Create());
            return WrittenRow();
        }

        await _registry.RegisterAsync(upper);
        (await PostAsync()).EnumerateObject().Select(p => p.Name).Should().Contain("Url").And.NotContain("url");

        await _registry.RegisterAsync(lower);
        (await PostAsync()).EnumerateObject().Select(p => p.Name).Should().Contain("url").And.NotContain("Url");
    }
}
