using MySqlConnector;

namespace Iverson.StarRocks;

public sealed class StarRocksHealthChecker(string connectionString) : IEngagementStoreHealthCheck
{
    private MySqlConnection CreateConnection() => new(connectionString);

    // Deliberately NOT routed through a readiness gate or circuit breaker: this backs the
    // k8s readiness probe (via /health), which must return quickly and let k8s re-poll on
    // its own cadence rather than block for a multi-minute cold-start budget.
    public async Task<StarRocksHealthStatus> CheckHealthAsync()
    {
        try
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync();

            await using (var cmd = new MySqlCommand("SELECT 1", conn))
                await cmd.ExecuteScalarAsync();

            return await AnyBackendAliveAsync(conn)
                ? StarRocksHealthStatus.Healthy
                : StarRocksHealthStatus.Unhealthy;
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
    internal static StarRocksHealthStatus ClassifyConnectionException(Exception ex) =>
        ex is MySqlException { ErrorCode: MySqlErrorCode.AccessDenied }
            ? StarRocksHealthStatus.AuthPending
            : StarRocksHealthStatus.Unhealthy;

    public async Task<bool> IsHealthyAsync() =>
        await CheckHealthAsync() == StarRocksHealthStatus.Healthy;

    internal static async Task<bool> AnyBackendAliveAsync(MySqlConnection conn, CancellationToken ct = default)
    {
        await using var cmd = new MySqlCommand("SHOW BACKENDS", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var aliveOrdinal = -1;
        while (await reader.ReadAsync(ct))
        {
            if (aliveOrdinal < 0)
                aliveOrdinal = reader.GetOrdinal("Alive");

            if (string.Equals(reader.GetString(aliveOrdinal), "true", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
