using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Tenancy;
using Iverson.Client.Contracts;
using Iverson.Sql;
using Iverson.StarRocks;

namespace Iverson.Api.Grpc;

public sealed class TenantLifecycleGrpcService(
    ITenantRepository tenantRepository,
    IAuthentikAdminClient authentikAdminClient,
    AuditLog auditLog) : Iverson.Client.Contracts.TenantLifecycleGrpcService.TenantLifecycleGrpcServiceBase
{
    public override async Task<Tenant> CreateTenant(CreateTenantRequest request, ServerCallContext context)
    {
        if (!TenantIdentifier.IsValid(request.TenantId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"'{request.TenantId}' is not a valid tenant id."));

        await tenantRepository.InsertAsync(request.TenantId, request.DisplayName, "active");

        try
        {
            await authentikAdminClient.CreateUserAsync(
                request.AdminUsername, request.AdminEmail, request.AdminInitialPassword,
                request.TenantId, ["tenant-admins"]);
        }
        catch
        {
            await tenantRepository.DeleteAsync(request.TenantId);
            throw;
        }

        auditLog.AdminOperation(context.GetHttpContext().User, "CreateTenant", request.TenantId);
        return new Tenant { TenantId = request.TenantId, DisplayName = request.DisplayName, Status = "active" };
    }

    public override async Task<ListTenantsResponse> ListTenants(ListTenantsRequest request, ServerCallContext context)
    {
        var tenants = await tenantRepository.ListAsync();
        var response = new ListTenantsResponse();
        response.Tenants.AddRange(tenants.Select(ToProto));
        return response;
    }

    public override Task<Tenant> SuspendTenant(SuspendTenantRequest request, ServerCallContext context) =>
        SetStatusAsync(request.TenantId, "suspended", "SuspendTenant", context);

    public override Task<Tenant> ReactivateTenant(ReactivateTenantRequest request, ServerCallContext context) =>
        SetStatusAsync(request.TenantId, "active", "ReactivateTenant", context);

    public override async Task<Empty> DeleteTenant(DeleteTenantRequest request, ServerCallContext context)
    {
        var tenant = await tenantRepository.GetAsync(request.TenantId)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Tenant '{request.TenantId}' not found."));
        await tenantRepository.UpdateStatusAsync(request.TenantId, "deleted");
        await authentikAdminClient.DeactivateAllUsersInTenantAsync(request.TenantId);
        auditLog.AdminOperation(context.GetHttpContext().User, "DeleteTenant", request.TenantId);
        return new Empty();
    }

    private async Task<Tenant> SetStatusAsync(string tenantId, string status, string operation, ServerCallContext context)
    {
        var existing = await tenantRepository.GetAsync(tenantId)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Tenant '{tenantId}' not found."));
        await tenantRepository.UpdateStatusAsync(tenantId, status);
        auditLog.AdminOperation(context.GetHttpContext().User, operation, tenantId);
        return ToProto(existing with { Status = status });
    }

    private static Tenant ToProto(TenantRow row) =>
        new() { TenantId = row.Id, DisplayName = row.DisplayName, Status = row.Status };
}
