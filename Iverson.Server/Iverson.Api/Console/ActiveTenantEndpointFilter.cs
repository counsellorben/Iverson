using Iverson.Api.Tenancy;

namespace Iverson.Api.Console;

/// <summary>
/// CSR round-10 #14: the admin console's endpoints refuse a caller whose tenant is not active,
/// as the gRPC surfaces already do. The rule is <c>ActingUserInterceptor</c>'s: a caller with a
/// <c>tenant_id</c> claim whose tenant is unknown, suspended or deleted gets 403. A caller with
/// no <c>tenant_id</c> claim, or an empty one (an operator's console token carries
/// <c>tenant_id: null</c>), passes.
/// </summary>
public sealed class ActiveTenantEndpointFilter(ITenantStatusCache tenantStatusCache) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var tenantId = context.HttpContext.User.FindFirst("tenant_id")?.Value;
        if (!string.IsNullOrEmpty(tenantId))
        {
            var status = await tenantStatusCache.GetStatusAsync(tenantId);
            if (status is null or "suspended" or "deleted")
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        return await next(context);
    }
}
