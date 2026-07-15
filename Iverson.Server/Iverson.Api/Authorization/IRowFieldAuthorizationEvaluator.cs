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
    /// </summary>
    IReadOnlySet<string>? AllowedFields);
