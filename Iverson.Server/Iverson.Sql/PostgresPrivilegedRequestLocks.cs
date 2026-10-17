using Dapper;
using Npgsql;

namespace Iverson.Sql;

/// <summary>
/// The worker's locks for privileged requests (CSR round 12 §5.1): session advisory locks, each on
/// its own connection for as long as it is held, released in the lease's
/// <see cref="IAsyncDisposable.DisposeAsync"/>.
/// <para>
/// A waiter holds no connection: it tries the lock every <see cref="PollInterval"/>, on a
/// connection it returns between tries, and keeps one only once it holds the lock. The worker
/// claims requests without a cap, so waiters parked inside a blocking <c>pg_advisory_lock</c>
/// could otherwise use up the pool. Waiters are therefore not served in arrival order: whichever
/// tries first after a release takes the lock.
/// </para>
/// <para>
/// The connections come from this instance's own pool, apart from the worker's shared one, so
/// that a lease whose unlock fails can clear that pool: its connection is then closed instead of
/// going back to the pool still holding the lock, and nothing else in the worker loses a connection.
/// </para>
/// <para>
/// The per-table keys elsewhere are <c>hashtext(name)::bigint</c> (<see cref="PostgresSchemaManager"/>,
/// <see cref="StartupDdl"/>): sign-extended <c>int4</c> values in [-2^31, 2^31). These keys sit above
/// that range, so they can never collide with a table's: the registration lock is 2^41, and a type
/// lock is 2^40 + hashtext(lower(type)), in [2^40 - 2^31, 2^40 + 2^31).
/// </para>
/// </summary>
public sealed class PostgresPrivilegedRequestLocks(string connectionString)
    : IPrivilegedRequestLocks, IAsyncDisposable, IDisposable
{
    internal const long RegistrationKey = 1L << 41;
    internal const long TypeKeyBase     = 1L << 40;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly NpgsqlDataSource _dataSource = NpgsqlDataSource.Create(connectionString);

    public Task<IAsyncDisposable> AcquireRegistrationAsync(CancellationToken ct) =>
        AcquireAsync("@Key", new { Key = RegistrationKey }, ct);

    // lower(): the registry resolves type names case-insensitively, so two spellings of one
    // type must share a lock.
    public Task<IAsyncDisposable> AcquireTypeAsync(string typeName, CancellationToken ct) =>
        AcquireAsync("@Base + hashtext(lower(@TypeName))", new { Base = TypeKeyBase, TypeName = typeName }, ct);

    // A waiter waits as long as the holder runs (a reconcile can take minutes), which is what
    // serialises requests; only the worker's stopping token ends the wait. Each try returns at
    // once, so no command waits and Npgsql's command timeout never applies.
    private async Task<IAsyncDisposable> AcquireAsync(string keySql, object param, CancellationToken ct)
    {
        while (true)
        {
            var conn = _dataSource.CreateConnection();
            try
            {
                await conn.OpenAsync(ct);

                // Not cancellable: a cancel landing after the server took the lock would throw
                // here, and the lock would be lost on a connection about to go back to the pool.
                if (await conn.ExecuteScalarAsync<bool>($"SELECT pg_try_advisory_lock({keySql})", param))
                    return new Lease(this, conn, keySql, param);
            }
            catch
            {
                // The try may have taken the lock before it failed.
                await DiscardAsync(conn);
                throw;
            }

            await conn.DisposeAsync();
            await Task.Delay(PollInterval, ct);
        }
    }

    // Clear flags every connection checked out of this pool to be closed when it is returned, and
    // closing the session releases every session lock it holds.
    private async ValueTask DiscardAsync(NpgsqlConnection conn)
    {
        _dataSource.Clear();
        await conn.DisposeAsync();
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();

    public void Dispose() => _dataSource.Dispose();

    private sealed class Lease(PostgresPrivilegedRequestLocks owner, NpgsqlConnection conn, string keySql, object param)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            // Unlock before the connection goes back to the pool: a pooled connection keeps any
            // session lock it still holds. If the unlock fails, the connection is closed instead.
            try
            {
                await conn.ExecuteAsync($"SELECT pg_advisory_unlock({keySql})", param);
            }
            catch
            {
                await owner.DiscardAsync(conn);
                throw;
            }

            await conn.DisposeAsync();
        }
    }
}
