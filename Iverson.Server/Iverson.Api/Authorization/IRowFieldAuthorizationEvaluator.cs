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
    IReadOnlySet<string>? AllowedFields);
