using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Iverson.Api.Grpc;

public sealed class AuditingAuthorizationMiddlewareResultHandler(AuditLog auditLog) : IAuthorizationMiddlewareResultHandler
{
    private static readonly HashSet<string> AuditedPolicies = ["SchemaAdmin", "Operator", "TenantAdmin"];
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context,
        AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden)
        {
            var policyNames = context.GetEndpoint()?.Metadata
                .GetOrderedMetadata<IAuthorizeData>()
                .Select(a => a.Policy)
                .Where(p => p is not null) ?? [];
            if (policyNames.Any(p => AuditedPolicies.Contains(p!)))
                auditLog.Denied(context.User, "Unauthorized", context.GetEndpoint()?.DisplayName ?? "unknown", null, "PolicyRejected");
        }
        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
