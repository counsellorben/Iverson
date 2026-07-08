namespace Iverson.StarRocks;

public enum StarRocksHealthStatus
{
    Healthy,

    // The iverson_app user doesn't exist yet — expected during a fresh Helm install, before
    // the StarRocks create-user post-install hook has run. See StarRocksRepository's
    // ClassifyConnectionException for why this must not fail the k8s readiness probe.
    AuthPending,

    Unhealthy
}
