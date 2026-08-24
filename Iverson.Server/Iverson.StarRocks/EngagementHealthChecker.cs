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
    /// Whether at least one backend is both alive AND reporting real disk capacity. Backs BOTH the
    /// k8s readiness probe above and <c>EngagementRepository</c>'s production
    /// <see cref="StarRocksReadinessGate"/>, so the two can never disagree about what "ready" means.
    ///
    /// <para><b>Alive alone was not enough, and it let both of those open too early.</b> A freshly
    /// started BE registers with the FE and flips <c>Alive</c> to <c>true</c> BEFORE its first disk
    /// heartbeat lands. In that window <c>SHOW BACKENDS</c> reports
    /// <c>AvailCapacity: 1.000 B</c> — observed directly against starrocks/allin1-ubuntu — and the
    /// FE rejects every query that touches table data with "Current available backends: [],
    /// backends without enough disk space: [1000x]". So on a cold start the readiness gate opened
    /// and the first real query failed anyway, and /health reported Healthy while k8s routed
    /// traffic to a pod whose StarRocks queries could not succeed. That signature was recorded
    /// twice during the tenant plan as an undiagnosed test flake; it is a production behaviour.</para>
    ///
    /// <para><b>A genuinely full disk now reports NOT ready, and that is correct.</b> If a backend
    /// really has less than megabyte-scale space, StarRocks itself refuses data queries with that
    /// same error — so reporting Unhealthy is the honest answer, and strictly better than reporting
    /// Healthy while every query fails.</para>
    /// </summary>
    internal static async Task<bool> AnyBackendReadyAsync(MySqlConnection conn, CancellationToken ct = default)
    {
        await using var cmd = new MySqlCommand("SHOW BACKENDS", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<(string Alive, string AvailCapacity)>();
        var aliveOrdinal = -1;
        var availOrdinal = -1;
        while (await reader.ReadAsync(ct))
        {
            if (aliveOrdinal < 0)
            {
                aliveOrdinal = reader.GetOrdinal("Alive");
                availOrdinal = reader.GetOrdinal("AvailCapacity");
            }

            rows.Add((reader.GetString(aliveOrdinal), reader.GetString(availOrdinal)));
        }

        return AnyBackendReady(rows);
    }

    /// <summary>
    /// The judgement itself, over rows already read — so it is unit-testable without a live
    /// StarRocks. Split out deliberately: a test that only graded
    /// <see cref="HasReportedDiskCapacity"/> would still pass with the capacity check DELETED from
    /// the caller, which is the whole defect being fixed here rather than a hypothetical.
    /// </summary>
    internal static bool AnyBackendReady(IEnumerable<(string Alive, string AvailCapacity)> rows) =>
        rows.Any(r =>
            string.Equals(r.Alive, "true", StringComparison.OrdinalIgnoreCase)
            && HasReportedDiskCapacity(r.AvailCapacity));

    /// <summary>
    /// True when <paramref name="availCapacity"/> — <c>SHOW BACKENDS</c>' AvailCapacity, which the
    /// FE formats for humans as e.g. "1.000 B" or "907.304 GB" — is at least megabyte scale. The
    /// UNIT is the whole signal: a value still expressed in bytes or kilobytes is the
    /// pre-heartbeat placeholder rather than a reading. Parsed as a unit rather than a number
    /// because the number is meaningless without it.
    ///
    /// <para>Internal so it can be tested directly: the input that matters occurs only inside a
    /// startup window no test can summon on demand.</para>
    /// </summary>
    internal static bool HasReportedDiskCapacity(string availCapacity) =>
        availCapacity.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [_, var unit]
        && (unit.Equals("MB", StringComparison.OrdinalIgnoreCase)
            || unit.Equals("GB", StringComparison.OrdinalIgnoreCase)
            || unit.Equals("TB", StringComparison.OrdinalIgnoreCase)
            || unit.Equals("PB", StringComparison.OrdinalIgnoreCase));
}
