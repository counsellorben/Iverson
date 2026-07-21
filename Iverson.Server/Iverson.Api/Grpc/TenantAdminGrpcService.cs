using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Tenancy;
using Iverson.Client.Contracts;

namespace Iverson.Api.Grpc;

// Delegated per-tenant admin service. Unlike TenantLifecycleGrpcService (an operator-facing
// service where the caller is trusted infrastructure/human-operator tooling that always sends
// the acting-user-impersonation header), this service's caller is a tenant-admin authenticating
// directly — e.g. a future browser admin UI over grpc-web — with no reason to ever send that
// header. So every RPC here reads the caller's own tenant from the PRIMARY authenticated
// principal (context.GetHttpContext().User's "tenant_id" claim), never IActingUserAccessor.
// Same for the AuditLog.AdminOperation actor parameter. This service also does its OWN inline
// tenant-suspension check (RequireActiveTenantAsync) rather than relying on
// ActingUserInterceptor's check, because that interceptor's check is gated on the acting-user
// header being present — which, per the above, this service's caller never sends, so the
// interceptor's check would never fire here. This inline duplication is intentional.
public sealed class TenantAdminGrpcService(
    IAuthentikAdminClient authentikAdminClient,
    ITenantStatusCache tenantStatusCache,
    AuditLog auditLog) : Iverson.Client.Contracts.TenantAdminGrpcService.TenantAdminGrpcServiceBase
{
    public override async Task<TenantUser> InviteUser(InviteUserRequest request, ServerCallContext context)
    {
        var tenantId = await RequireActiveTenantAsync(context);
        await authentikAdminClient.CreateUserAsync(request.Username, request.Email, request.InitialPassword, tenantId, []);
        auditLog.AdminOperation(context.GetHttpContext().User, "InviteUser", request.Username);
        return new TenantUser { Username = request.Username, Email = request.Email };
    }

    public override async Task<ListUsersResponse> ListUsers(ListUsersRequest request, ServerCallContext context)
    {
        var tenantId = await RequireActiveTenantAsync(context);
        var users = await authentikAdminClient.ListUsersByTenantAsync(tenantId);
        var response = new ListUsersResponse();
        response.Users.AddRange(users.Select(u => new TenantUser { UserId = u.Id, Username = u.Username, Email = u.Email }));
        return response;
    }

    public override async Task<Empty> RemoveUser(RemoveUserRequest request, ServerCallContext context)
    {
        var tenantId = await RequireActiveTenantAsync(context);
        await RequireUserInTenantAsync(request.UserId, tenantId);
        await authentikAdminClient.DeactivateUserAsync(request.UserId);
        auditLog.AdminOperation(context.GetHttpContext().User, "RemoveUser", request.UserId);
        return new Empty();
    }

    public override async Task<TenantUser> SetTenantAdmin(SetTenantAdminRequest request, ServerCallContext context)
    {
        var tenantId = await RequireActiveTenantAsync(context);
        var user = await RequireUserInTenantAsync(request.UserId, tenantId);
        if (request.Grant)
            await authentikAdminClient.AddGroupAsync(request.UserId, "tenant-admins");
        else
            await authentikAdminClient.RemoveGroupAsync(request.UserId, "tenant-admins");
        auditLog.AdminOperation(context.GetHttpContext().User, "SetTenantAdmin", request.UserId);
        return new TenantUser { UserId = user.Id, Username = user.Username, Email = user.Email };
    }

    private async Task<string> RequireActiveTenantAsync(ServerCallContext context)
    {
        var tenantId = context.GetHttpContext().User.FindFirst("tenant_id")?.Value
            ?? throw new RpcException(new Status(StatusCode.PermissionDenied, "No tenant_id claim."));
        var status = await tenantStatusCache.GetStatusAsync(tenantId);
        if (status is null or "suspended" or "deleted")
            throw new RpcException(new Status(StatusCode.PermissionDenied, $"Tenant '{tenantId}' is not active."));
        return tenantId;
    }

    // Cross-tenant escalation guard: user_id (unlike tenant_id) is always caller-supplied, so
    // without this check a tenant-admin of Tenant A could deactivate or promote/demote a user
    // in Tenant B by supplying that user's id. Looking the target up via the caller's OWN
    // tenant's user list — rather than trusting the request's tenant scoping — closes that gap.
    private async Task<AuthentikUser> RequireUserInTenantAsync(string userId, string tenantId)
    {
        var users = await authentikAdminClient.ListUsersByTenantAsync(tenantId);
        return users.FirstOrDefault(u => u.Id == userId)
            ?? throw new RpcException(new Status(StatusCode.PermissionDenied, "User does not belong to your tenant."));
    }
}
