using Iverson.StarRocks;

namespace Iverson.Api;

public sealed record ReadinessResult(bool Ready, bool FullyHealthy);

public static class ReadinessPolicy
{
    // StarRocks "auth pending" is expected during a fresh install: the create-user post-install
    // hook can only run after --wait succeeds on this very readiness probe, so failing readiness
    // on AuthPending would deadlock every first install forever. It's still reported unhealthy
    // in the response body (the caller's "starrocks" check stays false) for real observability —
    // only the k8s-facing readiness verdict (Ready, which drives the HTTP status code) tolerates it.
    public static ReadinessResult Evaluate(
        bool postgresHealthy, StarRocksHealthStatus starRocksStatus, bool qdrantHealthy, bool kafkaHealthy)
    {
        var ready = postgresHealthy && qdrantHealthy && kafkaHealthy
            && starRocksStatus != StarRocksHealthStatus.Unhealthy;
        var fullyHealthy = ready && starRocksStatus == StarRocksHealthStatus.Healthy;

        return new ReadinessResult(ready, fullyHealthy);
    }
}
