using System.Threading.RateLimiting;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Iverson.Api.Grpc;

public sealed class RateLimitInterceptor : Interceptor
{
    private readonly PartitionedRateLimiter<string> _limiter =
        PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 50_000,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueLimit = 0
            }));

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        Enforce(context);
        return await continuation(request, context);
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Enforce(context);
        await continuation(request, responseStream, context);
    }

    private void Enforce(ServerCallContext context)
    {
        var subject = context.GetHttpContext().User.FindFirst("sub")?.Value ?? "unknown";
        using var lease = _limiter.AttemptAcquire(subject);
        if (!lease.IsAcquired)
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "Rate limit exceeded."));
    }
}
