using System.Security.Claims;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Iverson.Api.Tenancy;

namespace Iverson.Api.Grpc;

/// <summary>
/// CSR round 12 §2.3. A server-streaming RPC is authorized once, when it starts, by the JwtBearer schemes
/// and <see cref="ActingUserInterceptor"/>, and can then run for minutes. Before each message, this
/// interceptor checks again, from the in-memory caches, that neither the primary principal's <c>sub</c>
/// nor the acting user's has been revoked, and that the acting user's tenant is still active. A revoked
/// principal ends the stream with <c>Unauthenticated</c>; a suspended, deleted or unknown tenant ends it
/// with <c>PermissionDenied</c> "Tenant '…' is not active.", as the unary paths answer. The caches hear of
/// a change within seconds (<see cref="AuthInvalidationListener"/>).
/// <para>
/// The acting user's tenant is the call's effective tenant: the data plane scopes every read to it
/// (<c>EntityAccess.ForTenant</c>), and <see cref="ActingUserInterceptor"/> checks the same tenant when the
/// call starts. A call without an acting user is re-checked for revocation only, as it was checked at the
/// start. Registered after <see cref="ActingUserInterceptor"/>, which has set the acting user by the time
/// this runs.
/// </para>
/// </summary>
public sealed class StreamRevalidationInterceptor(
    ITokenRevocationCache tokenRevocationCache,
    ITenantStatusCache tenantStatusCache) : Interceptor
{
    public override Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        var httpContext = context.GetHttpContext();
        var primary = httpContext.User;
        var actingUser = httpContext.RequestServices.GetRequiredService<IActingUserAccessor>().ActingUser;

        return continuation(
            request,
            new RevalidatingServerStreamWriter<TResponse>(
                responseStream, ct => RecheckAsync(primary, actingUser, ct), context.CancellationToken),
            context);
    }

    private async Task RecheckAsync(ClaimsPrincipal primary, ClaimsPrincipal? actingUser, CancellationToken cancellationToken)
    {
        if (await TokenRevocationCheck.IsRevokedAsync(tokenRevocationCache, primary, cancellationToken)
            || (actingUser is not null && await TokenRevocationCheck.IsRevokedAsync(tokenRevocationCache, actingUser, cancellationToken)))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Token has been revoked."));

        var tenantId = actingUser?.FindFirst("tenant_id")?.Value;
        if (tenantId is not null
            && await tenantStatusCache.GetStatusAsync(tenantId) is null or "suspended" or "deleted")
            throw new RpcException(new Status(StatusCode.PermissionDenied, $"Tenant '{tenantId}' is not active."));
    }
}

/// <summary>
/// The writer a server-streaming RPC writes through under <see cref="StreamRevalidationInterceptor"/>: it
/// re-checks before every message and passes <see cref="WriteOptions"/> through. Both <c>WriteAsync</c>
/// forms are implemented: the interface's default two-argument form throws when the token can be cancelled
/// (Grpc.Core.Api 2.84.0), and the streaming RPCs pass <c>context.CancellationToken</c>.
/// </summary>
internal sealed class RevalidatingServerStreamWriter<T>(
    IServerStreamWriter<T> inner,
    Func<CancellationToken, Task> recheck,
    CancellationToken callCancellation) : IServerStreamWriter<T>
{
    public WriteOptions? WriteOptions
    {
        get => inner.WriteOptions;
        set => inner.WriteOptions = value;
    }

    public async Task WriteAsync(T message)
    {
        await recheck(callCancellation);
        await inner.WriteAsync(message);
    }

    public async Task WriteAsync(T message, CancellationToken cancellationToken)
    {
        await recheck(cancellationToken);
        await inner.WriteAsync(message, cancellationToken);
    }
}
