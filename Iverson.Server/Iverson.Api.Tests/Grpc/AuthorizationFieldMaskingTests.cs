using System.Security.Claims;
using FluentAssertions;
using Grpc.Core;
using Google.Protobuf.WellKnownTypes;
using Iverson.Api.Authorization;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Grpc;

/// <summary>
/// Pins the interaction between the server force-setting an authoritative column and the payload
/// the .NET client actually sends.
/// <para>
/// The client serializes with a camelCase naming policy, so a payload arrives carrying
/// <c>tenantId</c>/<c>ownerId</c> while the schema's columns are <c>TenantId</c>/<c>OwnerId</c>.
/// Writing the canonical key without removing the camelCase one left BOTH in the Struct, and
/// <c>StructSerializer.SerializePayload</c> then UpperFirst()es every key into a Dictionary —
/// throwing "An item with the same key has already been added. Key: TenantId" and failing every
/// write with a bare Unknown gRPC status.
/// </para>
/// <para>
/// This was invisible to every existing test because they build payloads with canonical casing,
/// and invisible in the load test because the only identity that reached the tenant-stamping code
/// was the bypass one — every other caller was rejected by field authorization first.
/// </para>
/// </summary>
public sealed class AuthorizationFieldMaskingTests
{
    private static SchemaDescriptor Schema() => new()
    {
        TypeName      = "BenchmarkTag",
        TableName     = "benchmark_tags",
        KeyColumn     = new ColumnDescriptor("Id", "VARCHAR(255)", false),
        ScalarColumns =
        [
            new ColumnDescriptor("Name", "VARCHAR(255)", false),
            new ColumnDescriptor("OwnerId", "VARCHAR(255)", false),
            new ColumnDescriptor("TenantId", "VARCHAR(255)", false),
        ],
        FkColumns    = [],
        VectorFields = [],
        ChunkFields  = [],
        Relations    = [],
    };

    private static IRowFieldAuthorizationEvaluator EvaluatorReturning(AuthorizationDecision decision)
    {
        var evaluator = Substitute.For<IRowFieldAuthorizationEvaluator>();
        evaluator
            .Evaluate(Arg.Any<SchemaDescriptor>(), Arg.Any<ClaimsPrincipal?>(), Arg.Any<AuthorizationAction>())
            .Returns(decision);
        return evaluator;
    }

    private static void Enforce(Struct payload, AuthorizationDecision decision) =>
        AuthorizationFieldMasking.EnforceWriteAuthorization(
            EvaluatorReturning(decision),
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-1")], "test")),
            Schema(),
            payload,
            AuthorizationAction.Write,
            "Not authorized to create this entity.",
            existingRowJson: null,
            new AuditLog(NullLogger<AuditLog>.Instance));

    [Fact]
    public void EnforceWriteAuthorization_CamelCaseTenantKey_LeavesOnlyCanonicalKey()
    {
        var payload = new Struct
        {
            Fields =
            {
                ["id"]       = Value.ForString("tag-1"),
                ["name"]     = Value.ForString("wptag-1"),
                // What the .NET client actually sends — camelCase, colliding with "TenantId".
                ["tenantId"] = Value.ForString("client-supplied-tenant"),
            }
        };

        Enforce(payload, new AuthorizationDecision(
            Denied: false, OwnershipRequired: false, OwnerFieldName: "OwnerId", OwnerValue: null,
            AllowedFields: null, TenantColumn: "TenantId", TenantValue: "tenant-from-token"));

        payload.Fields.Should().NotContainKey("tenantId");
        payload.Fields["TenantId"].StringValue.Should().Be("tenant-from-token");

        // The actual failure mode: SerializePayload UpperFirst()es every key into a Dictionary,
        // which throws on a duplicate. This must not throw.
        var act = () => StructSerializer.SerializePayload(payload);
        act.Should().NotThrow();
    }

    [Fact]
    public void EnforceWriteAuthorization_CamelCaseOwnerKey_LeavesOnlyCanonicalKey()
    {
        var payload = new Struct
        {
            Fields =
            {
                ["id"]      = Value.ForString("tag-1"),
                ["ownerId"] = Value.ForString("client-supplied-owner"),
            }
        };

        Enforce(payload, new AuthorizationDecision(
            Denied: false, OwnershipRequired: true, OwnerFieldName: "OwnerId", OwnerValue: "owner-from-token",
            AllowedFields: null, TenantColumn: null, TenantValue: null));

        payload.Fields.Should().NotContainKey("ownerId");
        payload.Fields["OwnerId"].StringValue.Should().Be("owner-from-token");

        var act = () => StructSerializer.SerializePayload(payload);
        act.Should().NotThrow();
    }

    [Fact]
    public void EnforceWriteAuthorization_ServerValueWinsOverClientSuppliedValue()
    {
        // The whole point of force-setting these columns: they derive from the caller's token, so
        // a client that supplies its own tenant must not be able to influence the stored value.
        var payload = new Struct
        {
            Fields =
            {
                ["id"]       = Value.ForString("tag-1"),
                ["TenantId"] = Value.ForString("attacker-supplied-tenant"),
            }
        };

        Enforce(payload, new AuthorizationDecision(
            Denied: false, OwnershipRequired: false, OwnerFieldName: "OwnerId", OwnerValue: null,
            AllowedFields: null, TenantColumn: "TenantId", TenantValue: "tenant-from-token"));

        payload.Fields["TenantId"].StringValue.Should().Be("tenant-from-token");
    }

    [Fact]
    public void EnforceWriteAuthorization_FieldRestrictedCaller_NestedRelationStructNotRejected()
    {
        // Exercises the real evaluator (not a mock) so this test depends on
        // RowFieldAuthorizationEvaluator actually admitting relation names into the write-side
        // AllowedFields set. Without that production change, "Author" would be missing from
        // AllowedFields and RejectDisallowedFields would throw InvalidArgument for the nested
        // relation struct below.
        var schema = new SchemaDescriptor
        {
            TypeName      = "BenchmarkTag",
            TableName     = "benchmark_tags",
            KeyColumn     = new ColumnDescriptor("Id", "VARCHAR(255)", false),
            ScalarColumns =
            [
                new ColumnDescriptor("Name", "VARCHAR(255)", false),
                new ColumnDescriptor("OwnerId", "VARCHAR(255)", false),
                new ColumnDescriptor("TenantId", "VARCHAR(255)", false),
            ],
            FkColumns    = [new ForeignKeyDescriptor("AuthorId", "Author")],
            VectorFields = [],
            ChunkFields  = [],
            Relations    = [new RelationDescriptor("Author", RelationKind.ManyToOne, "Author", "AuthorId")],
            TenantColumn = "TenantId",
            Authorization = new AuthorizationRules(
                "OwnerId",
                new List<RowPermission>(),
                new List<FieldPermission>
                {
                    // Excludes an unrelated field for a role the caller doesn't hold, which is
                    // what forces the evaluator down the non-null AllowedFields branch.
                    new("Name", new List<string>(), new List<string> { "editor" })
                }),
        };

        var payload = new Struct
        {
            Fields =
            {
                ["id"]       = Value.ForString("tag-1"),
                ["AuthorId"] = Value.ForString("author-1"),
                ["Author"]   = Value.ForStruct(new Struct
                {
                    Fields = { ["Id"] = Value.ForString("author-1"), ["Name"] = Value.ForString("A. Author") }
                }),
            }
        };

        var act = () => AuthorizationFieldMasking.EnforceWriteAuthorization(
            new RowFieldAuthorizationEvaluator(),
            new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", "user-1"), new Claim("tenant_id", "tenant-1")], "test")),
            schema,
            payload,
            AuthorizationAction.Write,
            "Not authorized to create this entity.",
            existingRowJson: null,
            new AuditLog(NullLogger<AuditLog>.Instance));

        act.Should().NotThrow();
    }

    // ── Server-owned tenant column strip ──────────────────────────────────────
    //
    // Decision 6 of the remove-IversonTenant plan: __TenantId never appears on the wire.
    // MaskDisallowedFields is the shared cover for all five outbound read paths
    // (ObjectRetrievalGrpcService.Get/GetMany, ObjectSearchGrpcService.SearchSimilar,
    // EntityRelationResolver, ObjectMappingGrpcService.Get), so the strip lives here — and it
    // must sit BEFORE the `allowedFields is null` early return, because a schema with no field
    // permissions is precisely the case where that return would let the column through.

    private static Struct PayloadWithTenant(string tenantKey = SchemaDescriptor.TenantColumnName) =>
        new()
        {
            Fields =
            {
                ["Id"]      = Value.ForString("tag-1"),
                ["Name"]    = Value.ForString("wptag-1"),
                [tenantKey] = Value.ForString("tenant-from-token"),
            }
        };

    [Fact]
    public void MaskDisallowedFields_NullAllowedFields_StillRemovesTheTenantColumn()
    {
        // The unrestricted caller — the early-return path. This is the case the strip's placement
        // exists for; if the strip moved below the guard, this is the test that reddens.
        var payload = PayloadWithTenant();

        AuthorizationFieldMasking.MaskDisallowedFields(payload, allowedFields: null);

        payload.Fields.Should().NotContainKey(SchemaDescriptor.TenantColumnName);
        payload.Fields.Should().ContainKey("Name"); // nothing else was touched
        payload.Fields.Should().ContainKey("Id");
    }

    [Fact]
    public void MaskDisallowedFields_WithAllowedFields_RemovesTheTenantColumn()
    {
        // The field-restricted caller. Note the tenant column is listed as ALLOWED here — a
        // hand-built AllowedFields that happens to contain it must still not put it on the wire,
        // so the strip cannot be implemented by relying on the allow-list to omit it.
        var payload = PayloadWithTenant();

        AuthorizationFieldMasking.MaskDisallowedFields(
            payload,
            new HashSet<string> { "Id", "Name", SchemaDescriptor.TenantColumnName });

        payload.Fields.Should().NotContainKey(SchemaDescriptor.TenantColumnName);
        payload.Fields.Should().ContainKey("Name");
    }

    [Theory]
    [InlineData("__TenantId")]
    [InlineData("__tenantId")]
    [InlineData("__TENANTID")]
    public void MaskDisallowedFields_RemovesTheTenantColumnInAnyCasing(string tenantKey)
    {
        var payload = PayloadWithTenant(tenantKey);

        AuthorizationFieldMasking.MaskDisallowedFields(payload, allowedFields: null);

        payload.Fields.Should().NotContainKey(tenantKey);
        payload.Fields.Keys.Should().NotContain(k => k.ToUpperInvariant().Contains("TENANTID"));
    }

    [Fact]
    public void MaskDisallowedFields_LeavesAClientDeclaredTenantIdColumnAlone()
    {
        // A legacy schema whose tenant boundary sits on a CLIENT-declared "TenantId" column is a
        // different thing: that name is part of the client's own contract and has always been on
        // the wire. Only the reserved __TenantId spelling is server-owned.
        var payload = new Struct
        {
            Fields =
            {
                ["Id"]       = Value.ForString("tag-1"),
                ["TenantId"] = Value.ForString("tenant-from-token"),
            }
        };

        AuthorizationFieldMasking.MaskDisallowedFields(payload, allowedFields: null);

        payload.Fields.Should().ContainKey("TenantId");
    }

    [Fact]
    public void EnforceWriteAuthorization_ExistingRow_ForceSetsTheTenantColumnIntoThePayload()
    {
        // The update branch previously only VALIDATED the tenant column and left the payload
        // alone. With the column server-owned, a client payload never carries it — so the
        // serialized payload published to Kafka would carry no tenant value, and
        // EngagementRepository.UpsertAsync (StarRocks Primary Key model = full-row replace) would
        // reset the projected row's tenant column to NULL, making every subsequent StarRocks read
        // for that tenant return nothing. Force-setting it keeps the projection payload complete.
        var payload = new Struct
        {
            Fields =
            {
                ["Id"]   = Value.ForString("tag-1"),
                ["Name"] = Value.ForString("wptag-1"),
            }
        };

        AuthorizationFieldMasking.EnforceWriteAuthorization(
            EvaluatorReturning(new AuthorizationDecision(
                Denied: false, OwnershipRequired: false, OwnerFieldName: null, OwnerValue: null,
                AllowedFields: null, TenantColumn: "TenantId", TenantValue: "tenant-from-token")),
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-1")], "test")),
            Schema(),
            payload,
            AuthorizationAction.Write,
            "Not authorized to update this entity.",
            existingRowJson: """{"Id":"tag-1","Name":"old","TenantId":"tenant-from-token"}""",
            new AuditLog(NullLogger<AuditLog>.Instance));

        payload.Fields["TenantId"].StringValue.Should().Be("tenant-from-token");
    }

    // ── Task 4 rejection 3 of 3: the server-owned tenant column in a write payload ────────────

    private static SchemaDescriptor ServerOwnedTenantSchema() => new()
    {
        TypeName      = "BenchmarkTag",
        TableName     = "benchmark_tags",
        KeyColumn     = new ColumnDescriptor("Id", "UUID", false),
        ScalarColumns =
        [
            new ColumnDescriptor("Name", "VARCHAR(255)", false),
            new ColumnDescriptor(SchemaDescriptor.TenantColumnName, "TEXT", false),
        ],
        FkColumns    = [],
        VectorFields = [],
        ChunkFields  = [],
        Relations    = [],
        TenantColumn = SchemaDescriptor.TenantColumnName,
    };

    private static RpcException EnforceAndCatch(Struct payload, AuthorizationDecision decision, string? existingRowJson)
    {
        var act = () => AuthorizationFieldMasking.EnforceWriteAuthorization(
            EvaluatorReturning(decision),
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-1")], "test")),
            ServerOwnedTenantSchema(),
            payload,
            AuthorizationAction.Write,
            "Not authorized to create this entity.",
            existingRowJson,
            new AuditLog(NullLogger<AuditLog>.Instance));

        return act.Should().Throw<RpcException>().Which;
    }

    private static AuthorizationDecision Allowed() => new(
        Denied: false, OwnershipRequired: false, OwnerFieldName: null, OwnerValue: null,
        AllowedFields: null, TenantColumn: SchemaDescriptor.TenantColumnName, TenantValue: "tenant-from-token");

    [Fact]
    public void EnforceWriteAuthorization_PayloadCarryingTheServerOwnedTenantColumn_ThrowsInvalidArgument()
    {
        var payload = new Struct
        {
            Fields =
            {
                ["Id"] = Value.ForString("tag-1"),
                [SchemaDescriptor.TenantColumnName] = Value.ForString("attacker-supplied-tenant"),
            }
        };

        var ex = EnforceAndCatch(payload, Allowed(), existingRowJson: null);

        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
        // The MESSAGE, not just the status: RejectDisallowedFields also throws InvalidArgument
        // from this same method, so a status-only assertion would not distinguish them.
        ex.Status.Detail.Should().Contain("__TenantId");
        ex.Status.Detail.Should().Contain("reserved server-owned column");
    }

    [Fact]
    public void EnforceWriteAuthorization_UpdatePayloadCarryingTheServerOwnedTenantColumn_ThrowsInvalidArgument()
    {
        // Unconditional and independent of the update-branch immutability check below it, which
        // only fires when the smuggled value DIFFERS from the caller's own tenant. Here the value
        // matches exactly, so the immutability check would wave it through.
        var payload = new Struct
        {
            Fields =
            {
                ["Id"] = Value.ForString("tag-1"),
                [SchemaDescriptor.TenantColumnName] = Value.ForString("tenant-from-token"),
            }
        };

        var ex = EnforceAndCatch(
            payload, Allowed(),
            existingRowJson: """{"Id":"tag-1","Name":"old","__TenantId":"tenant-from-token"}""");

        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Status.Detail.Should().Contain("reserved server-owned column");
    }

    [Fact]
    public void EnforceWriteAuthorization_RecasedTenantColumnInPayload_ThrowsInvalidArgument()
    {
        // Case-insensitive, so the reservation cannot be smuggled past by re-casing — which
        // SetAuthoritativeField's canonical-casing fixup would otherwise absorb silently.
        var payload = new Struct
        {
            Fields =
            {
                ["Id"] = Value.ForString("tag-1"),
                ["__tenantid"] = Value.ForString("attacker-supplied-tenant"),
            }
        };

        var ex = EnforceAndCatch(payload, Allowed(), existingRowJson: null);

        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Status.Detail.Should().Contain("__tenantid");
    }

    [Fact]
    public void EnforceWriteAuthorization_DeniedCallerSmugglingTheTenantColumn_StillGetsInvalidArgument()
    {
        // Ruling 17: the check is a malformed-request check independent of identity, so it runs
        // BEFORE authEvaluator.Evaluate. Placed after it, this caller would get PermissionDenied
        // and the malformed field would be masked entirely — making the InvalidArgument contract
        // conditional on authorization.
        var payload = new Struct
        {
            Fields =
            {
                ["Id"] = Value.ForString("tag-1"),
                [SchemaDescriptor.TenantColumnName] = Value.ForString("attacker-supplied-tenant"),
            }
        };

        var ex = EnforceAndCatch(
            payload,
            new AuthorizationDecision(
                Denied: true, OwnershipRequired: false, OwnerFieldName: null, OwnerValue: null,
                AllowedFields: null, TenantColumn: null, TenantValue: null),
            existingRowJson: null);

        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.StatusCode.Should().NotBe(StatusCode.PermissionDenied);
        ex.Status.Detail.Should().Contain("reserved server-owned column");
    }

    [Fact]
    public void EnforceWriteAuthorization_PayloadWithoutTheTenantColumn_IsUnaffected()
    {
        // The negative control: the guard must not reject an ordinary payload, and the tenant
        // column the server force-sets afterwards must survive its own check.
        var payload = new Struct
        {
            Fields = { ["Id"] = Value.ForString("tag-1"), ["Name"] = Value.ForString("wptag-1") }
        };

        var act = () => AuthorizationFieldMasking.EnforceWriteAuthorization(
            EvaluatorReturning(Allowed()),
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-1")], "test")),
            ServerOwnedTenantSchema(),
            payload,
            AuthorizationAction.Write,
            "Not authorized to create this entity.",
            existingRowJson: null,
            new AuditLog(NullLogger<AuditLog>.Instance));

        act.Should().NotThrow();
        payload.Fields[SchemaDescriptor.TenantColumnName].StringValue.Should().Be("tenant-from-token");
    }
}
