using System.Security.Claims;
using Iverson.Api.Schema;

namespace Iverson.Api.Authorization;

public enum AuthorizationAction { Read, Write, Delete }

public interface IRowFieldAuthorizationEvaluator
{
    AuthorizationDecision Evaluate(
        SchemaDescriptor schema,
        ClaimsPrincipal? actingUser,
        AuthorizationAction action);
}

public sealed record AuthorizationDecision(
    bool Denied,
    bool OwnershipRequired,
    string? OwnerFieldName,
    string? OwnerValue,
    /// <summary>
    /// Null means unrestricted. Non-null is the full set of field names the caller may access for
    /// this action — the key column, every scalar column, every FK column, and every vector/chunk
    /// field's source property name — minus whichever of those a <c>FieldPermission</c> excluded.
    /// The key column itself is always included, even if a <c>FieldPermission</c> names it.
    /// For write actions only, relation property names are also included when their FK column is
    /// writable (or unconditionally for <c>OneToMany</c>, which has no local FK column); this does
    /// not apply to read, since <c>AllowedFields</c> also governs search filter/sort/vector
    /// authorization there.
    /// </summary>
    IReadOnlySet<string>? AllowedFields,
    /// <summary>
    /// DELIBERATELY NULLABLE, and NOT for the reason <see cref="Schema.SchemaDescriptor.TenantColumn"/>
    /// used to be. Null here does not mean "legacy pre-cutover schema" — it means THIS DECISION
    /// ESTABLISHED NO TENANT BOUNDARY, which is a live, everyday outcome against a perfectly
    /// current schema. Every early return in <see cref="RowFieldAuthorizationEvaluator.Evaluate"/>
    /// (no authorization rules, no acting user, no tenant column, no <c>tenant_id</c> claim, no
    /// owner field and no bypass role) pairs <c>Denied = true</c> with a null TenantColumn and a
    /// null TenantValue. Only the single non-denied return carries a real column name.
    /// <para>
    /// Consequence for callers: a <c>TenantColumn is not null</c> guard here is NOT dead code
    /// waiting to be removed. Three of them — <c>ObjectRetrievalGrpcService.Get</c>,
    /// <c>ObjectMappingGrpcService.Get</c> and <c>ObjectMappingGrpcService.Delete</c> — compute
    /// <c>tenantMismatch</c> BEFORE testing <c>Denied</c>, so they reach this property on denied
    /// decisions on every denied read. Removing the guard makes those a
    /// <c>Struct.Fields.TryGetValue(null)</c>, i.e. a thrown ArgumentNullException surfacing as a
    /// gRPC Unknown, in place of the clean "not found" they return today.
    /// </para>
    /// </summary>
    string? TenantColumn,
    string? TenantValue);
