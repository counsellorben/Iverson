using System.Security.Claims;
using FluentAssertions;
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
}
