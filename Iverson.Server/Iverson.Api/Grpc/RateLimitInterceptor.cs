using System.Threading.RateLimiting;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Iverson.Api;

namespace Iverson.Api.Grpc;

public sealed class RateLimitInterceptor(ILogger<RateLimitInterceptor> logger) : Interceptor
{
    // CSR round 12 §2.1: the tenant-admin and tenant-lifecycle RPCs read and write the IdP and the
    // tenant table, so they do not share the data plane's budget. They get 30 calls per minute,
    // partitioned by (service, sub): each of the two services has its own bucket per caller.
    private static readonly string[] AdminServices =
    [
        Iverson.Client.Contracts.TenantAdminGrpcService.Descriptor.FullName,
        Iverson.Client.Contracts.TenantLifecycleGrpcService.Descriptor.FullName,
    ];

    private readonly PartitionedRateLimiter<string> _limiter =
        PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 50_000,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueLimit = 0
            }));

    private readonly PartitionedRateLimiter<(string Service, string Subject)> _adminLimiter =
        PartitionedRateLimiter.Create<(string Service, string Subject), (string Service, string Subject)>(key =>
            RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 30,
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
        // The service of the endpoint that routing matched, not the request path: routing matches the
        // path's literals case-insensitively, while ServerCallContext.Method is the path as sent.
        var matched = context.GetHttpContext().GetEndpoint()?.Metadata
            .GetMetadata<global::Grpc.AspNetCore.Server.GrpcMethodMetadata>()?.Method.ServiceName;
        var adminService = AdminServices.FirstOrDefault(service => service == matched);
        using var lease = adminService is null
            ? _limiter.AttemptAcquire(subject)
            : _adminLimiter.AttemptAcquire((adminService, subject));
        if (!lease.IsAcquired)
        {
            logger.LogWarning("[RateLimit] Rejected gRPC call for subject {Subject}", subject.SanitizeForLog());
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "Rate limit exceeded."));
        }
    }
}
