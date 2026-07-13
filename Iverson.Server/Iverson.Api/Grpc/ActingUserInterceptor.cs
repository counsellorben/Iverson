using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.AspNetCore.Authentication;

namespace Iverson.Api.Grpc;

public sealed class ActingUserInterceptor(ILogger<ActingUserInterceptor> logger) : Interceptor
{
    public const string MetadataKey = "x-acting-user-authorization";

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        await ValidateActingUserAsync(context);
        return await continuation(request, context);
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        await ValidateActingUserAsync(context);
        await continuation(request, responseStream, context);
    }

    private async Task ValidateActingUserAsync(ServerCallContext context)
    {
        var header = context.RequestHeaders.Get(MetadataKey)?.Value;
        if (string.IsNullOrEmpty(header))
            return;

        var httpContext = context.GetHttpContext();
        var result = await httpContext.AuthenticateAsync("ActingUser");
        if (!result.Succeeded || result.Principal is null)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Acting-user token is invalid."));

        httpContext.RequestServices.GetRequiredService<IActingUserAccessor>().ActingUser = result.Principal;

        var serviceSubject    = httpContext.User.FindFirst("sub")?.Value ?? "unknown";
        var actingUserSubject = result.Principal.FindFirst("sub")?.Value ?? "unknown";
        logger.LogInformation(
            "service {ServiceAccountSubject} acting as user {ActingUserSub} called {Method}",
            serviceSubject, actingUserSubject, context.Method);
    }
}
