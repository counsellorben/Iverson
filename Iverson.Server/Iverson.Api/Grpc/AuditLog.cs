using System.Security.Claims;

namespace Iverson.Api.Grpc;

public sealed class AuditLog(ILogger<AuditLog> logger)
{
    public void Denied(ClaimsPrincipal? actor, string action, string resourceType, string? resourceKey, string reason) =>
        logger.LogWarning(
            "[Audit.Denied] actor={Actor} tenant={Tenant} action={Action} resourceType={ResourceType} resourceKey={ResourceKey} reason={Reason}",
            actor?.FindFirst("sub")?.Value ?? "unknown",
            actor?.FindFirst("tenant_id")?.Value ?? "unknown",
            action, resourceType.SanitizeForLog(), resourceKey?.SanitizeForLog(), reason);

    public void AdminOperation(ClaimsPrincipal actor, string operation, string? detail) =>
        logger.LogInformation(
            "[Audit.AdminOperation] actor={Actor} operation={Operation} detail={Detail}",
            actor.FindFirst("sub")?.Value ?? "unknown", operation, detail?.SanitizeForLog());
}
