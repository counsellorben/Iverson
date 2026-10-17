using Dapper;
using Npgsql;

namespace Iverson.Sql;

/// <summary>
/// The worker's locks for privileged requests (CSR round 12 §5.1): session advisory locks, each on
/// its own pooled connection for as long as it is held, released in <see cref="IAsyncDisposable.DisposeAsync"/>.
/// <para>
/// The per-table keys elsewhere are <c>hashtext(name)::bigint</c> (<see cref="PostgresSchemaManager"/>,
/// <see cref="StartupDdl"/>): sign-extended <c>int4</c> values in [-2^31, 2^31). These keys sit above
/// that range, so they can never collide with a table's: the registration lock is 2^41, and a type
/// lock is 2^40 + hashtext(lower(type)), in [2^40 - 2^31, 2^40 + 2^31).
/// </para>
/// </summary>
public sealed class PostgresPrivilegedRequestLocks(string connectionString) : IPrivilegedRequestLocks
{
    internal const long RegistrationKey = 1L << 41;
    internal const long TypeKeyBase     = 1L << 40;

    public Task<IAsyncDisposable> AcquireRegistrationAsync(CancellationToken ct) =>
        AcquireAsync("@Key", new { Key = RegistrationKey }, ct);

    // lower(): the registry resolves type names case-insensitively, so two spellings of one
    // type must share a lock.
    public Task<IAsyncDisposable> AcquireTypeAsync(string typeName, CancellationToken ct) =>
        AcquireAsync("@Base + hashtext(lower(@TypeName))", new { Base = TypeKeyBase, TypeName = typeName }, ct);

    private async Task<IAsyncDisposable> AcquireAsync(string keySql, object param, CancellationToken ct)
    {
        var conn = new NpgsqlConnection(connectionString);
        try
        {
            await conn.OpenAsync(ct);
            // No command timeout: a waiter waits as long as the holder runs (a reconcile can take
            // minutes), which is what serialises requests. Only the worker's stopping token ends
            // the wait. Npgsql's default would cancel the wait after 30 s.
            await conn.ExecuteAsync(new CommandDefinition(
                $"SELECT pg_advisory_lock({keySql})", param, commandTimeout: 0, cancellationToken: ct));
            return new Lease(conn, keySql, param);
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    private sealed class Lease(NpgsqlConnection conn, string keySql, object param) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            // Unlock before the connection goes back to the pool: a pooled connection keeps any
            // session lock it still holds.
            try
            {
                await conn.ExecuteAsync($"SELECT pg_advisory_unlock({keySql})", param);
            }
            finally
            {
                await conn.DisposeAsync();
            }
        }
    }
}
