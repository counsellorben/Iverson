namespace Iverson.StarRocks;

/// <summary>
/// Registered in place of EngagementHealthChecker when Engagement__Enabled is false.
///
/// Unlike DisabledEngagementStoreSearchService (which throws — correct for a query path that
/// is genuinely unreachable), this must NOT throw: <c>/health</c> calls CheckHealthAsync() on
/// every request via Task.WhenAll, and the api/worker Deployments' readinessProbe polls
/// <c>/health</c>. A throwing health check would make the pod permanently unready on the
/// <c>engagementEnabled: false</c> profile (values-laptop.yaml). Instead it reports a benign
/// status; Program.cs's /health handler already takes the "disabled" branch for the
/// user-visible `checks.starrocks` field whenever `Engagement:Enabled` is false, and
/// ReadinessPolicy.Evaluate ignores the StarRocks status entirely in that case — so the value
/// returned here never actually drives the readiness verdict, it only needs to exist and not
/// throw.
/// </summary>
internal sealed class DisabledEngagementStoreHealthCheck : IEngagementStoreHealthCheck
{
    public Task<EngagementHealthStatus> CheckHealthAsync() =>
        Task.FromResult(EngagementHealthStatus.Healthy);

    public Task<bool> IsHealthyAsync() =>
        Task.FromResult(true);
}
