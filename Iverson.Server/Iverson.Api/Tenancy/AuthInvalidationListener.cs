using System.Diagnostics.Metrics;
using Iverson.Sql;
using Npgsql;

namespace Iverson.Api.Tenancy;

/// <summary>
/// CSR round 12 §2.3: makes a revocation or a tenant-status change, written by any api replica or by the
/// worker's legacy-tenant seed, reach this replica's <see cref="ITokenRevocationCache"/> and
/// <see cref="ITenantStatusCache"/> within seconds instead of after their 30 s TTL.
/// <para>
/// One dedicated connection per replica, opened from the replica's own Postgres connection string with
/// <c>Keepalive=30</c> and without pooling, LISTENs on <see cref="AuthInvalidation.Channel"/> and waits
/// with <see cref="NpgsqlConnection.WaitAsync(CancellationToken)"/>. Every notification drops both caches;
/// its payload is only a hint. Every write to either table notifies in its own statement
/// (<see cref="AuthInvalidation"/>).
/// </para>
/// <para>
/// A notification sent while this connection is down is never replayed, so on every connect and reconnect
/// the listener issues <c>LISTEN</c> first and then drops both caches. Reconnects back off from 1 s to
/// 30 s. The TTL stays the backstop, and readiness does not depend on the listener: a lost connection is
/// logged and counted (<c>auth_invalidation.listener_connections_lost</c>), never fatal.
/// </para>
/// </summary>
public sealed class AuthInvalidationListener(
    IConfiguration configuration,
    ITokenRevocationCache tokenRevocationCache,
    ITenantStatusCache tenantStatusCache,
    ILogger<AuthInvalidationListener> logger) : BackgroundService
{
    internal const string MeterName = "Iverson.Api.Tenancy";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    // Declared after Meter: static field initializers run in source order.
    private static readonly Counter<long> ConnectionsLost = Meter.CreateCounter<long>(
        "auth_invalidation.listener_connections_lost",
        description: "Times this api replica's auth-invalidation listener lost, or could not open, its Postgres connection; its caches refresh on their 30 s TTL until it reconnects");

    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = ListenerConnectionString(configuration.GetConnectionString("Postgres")!);
        var retryDelay = FirstRetryDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                connection.Notification += (_, _) => InvalidateCaches();
                await connection.OpenAsync(stoppingToken);
                await using (var listen = new NpgsqlCommand($"LISTEN {AuthInvalidation.Channel}", connection))
                    await listen.ExecuteNonQueryAsync(stoppingToken);

                // Whatever was written while this replica was not listening was never delivered to it.
                InvalidateCaches();
                retryDelay = FirstRetryDelay;

                while (true)
                    await connection.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                ConnectionsLost.Add(1);
                logger.LogWarning(ex,
                    "The auth-invalidation listener lost its Postgres connection; reconnecting in {RetryDelay}. "
                    + "Until then this replica's revocation and tenant-status caches refresh on their 30 s TTL.",
                    retryDelay);
            }

            try
            {
                await Task.Delay(retryDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, MaxRetryDelay.Ticks));
        }
    }

    /// <summary>The replica's own connection string, with a 30 s keepalive while it waits and no pooling.</summary>
    internal static string ListenerConnectionString(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { KeepAlive = 30, Pooling = false }.ConnectionString;

    private void InvalidateCaches()
    {
        tokenRevocationCache.Invalidate();
        tenantStatusCache.InvalidateAll();
    }
}
