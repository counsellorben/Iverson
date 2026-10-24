using MySqlConnector;

namespace Iverson.StarRocks;

public sealed class EngagementHealthChecker(string connectionString) : IEngagementStoreHealthCheck
{
    private MySqlConnection CreateConnection() => new(connectionString);

    // Deliberately NOT routed through a readiness gate or circuit breaker: this backs the
    // k8s readiness probe (via /health), which must return quickly and let k8s re-poll on
    // its own cadence rather than block for a multi-minute cold-start budget.
    public async Task<EngagementHealthStatus> CheckHealthAsync()
    {
        try
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync();

            await using (var cmd = new MySqlCommand("SELECT 1", conn))
                await cmd.ExecuteScalarAsync();

            return await AnyBackendReadyAsync(conn)
                ? EngagementHealthStatus.Healthy
                : EngagementHealthStatus.Unhealthy;
        }
        catch (Exception ex)
        {
            return ClassifyConnectionException(ex);
        }
    }

    // iverson_app doesn't exist until the StarRocks create-user post-install Helm hook runs, and
    // Helm only runs post-install hooks after --wait succeeds on the main manifest (which
    // includes this process's own readinessProbe) — so treating this specific failure as
    // blocking readiness would deadlock every fresh install forever. Any other failure (down,
    // wrong host) still correctly reports Unhealthy. Note AccessDenied is wire-level ambiguous —
    // it also covers a wrong password for an already-created iverson_app user — so that specific
    // misconfiguration is deliberately tolerated for readiness too; the /health response body
    // still reports checks.starrocks=false/"degraded" the whole time, so body-reading monitoring
    // still catches it. internal (not private) so Iverson.StarRocks.Tests — which has
    // InternalsVisibleTo access — can test the classification directly without a live connection.
    internal static EngagementHealthStatus ClassifyConnectionException(Exception ex) =>
        ex is MySqlException { ErrorCode: MySqlErrorCode.AccessDenied }
            ? EngagementHealthStatus.AuthPending
            : EngagementHealthStatus.Unhealthy;

    public async Task<bool> IsHealthyAsync() =>
        await CheckHealthAsync() == EngagementHealthStatus.Healthy;

    /// <summary>
    /// CSR round 12 §3.5: the backend probe. A two-row table function runs on a backend, so it returns
    /// both rows only while one is alive, and it needs no privilege: <c>SHOW BACKENDS</c> needed
    /// <c>OPERATE ON SYSTEM</c>, which <c>iverson_app</c> no longer holds. With no backend alive the FE
    /// refuses it at once ("Backend node not found"); the timeout bounds a backend that hangs.
    /// </summary>
    internal const string BackendProbeSql = "SELECT * FROM TABLE(generate_series(1,2))";

    internal const int BackendProbeTimeoutSeconds = 5;

    internal static MySqlCommand CreateBackendProbe(MySqlConnection conn) =>
        new(BackendProbeSql, conn) { CommandTimeout = BackendProbeTimeoutSeconds };

    /// <summary>
    /// Whether a backend answers a query: both of the probe's rows came back. Backs BOTH the k8s
    /// readiness probe above and <c>EngagementRepository</c>'s production
    /// <see cref="StarRocksReadinessGate"/>, so the two can never disagree about what "ready" means.
    /// A failure, the probe's timeout included, throws; the callers count that as not ready.
    ///
    /// <para>It no longer waits for a backend to report megabyte-scale disk capacity, which only
    /// <c>SHOW BACKENDS</c> shows (spec §7): on a cold start a data query can still meet "backends
    /// without enough disk space" in the moments after the probe first succeeds.</para>
    /// </summary>
    internal static async Task<bool> AnyBackendReadyAsync(MySqlConnection conn, CancellationToken ct = default)
    {
        await using var cmd = CreateBackendProbe(conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var rows = 0;
        while (await reader.ReadAsync(ct))
            rows++;

        return rows == 2;
    }
}
