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
    string? TenantColumn,
    string? TenantValue);
