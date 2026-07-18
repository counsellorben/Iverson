using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Iverson.Sql;

public class PostgresRepository(
    string connectionString,
    ILogger<PostgresRepository> logger) : IRecordStoreQueryExecutor, IRecordStoreTransactionRunner
{
    private NpgsqlConnection CreateConnection() => new(connectionString);

    public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null, bool tenantScoped = false, string? tenantId = null)
    {
        using var activity = Telemetry.Source.StartActivity("db.query", ActivityKind.Client);
        activity?.SetTag("db.system", "postgresql");
        activity?.SetTag("db.statement", sql);

        logger.LogDebug("Executing query: {Sql}", sql);

        try
        {
            IEnumerable<T> results;
            if (tenantScoped)
            {
                results = await RunTenantScopedAsync(
                    tenantId, (conn, tx) => conn.QueryAsync<T>(sql, param, tx));
            }
            else
            {
                await using var conn = CreateConnection();
                results = await conn.QueryAsync<T>(sql, param);
            }
            activity?.SetStatus(ActivityStatusCode.Ok);
            return results;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.RecordException(ex);
            throw;
        }
    }

    public async Task<int> ExecuteAsync(string sql, object? param = null, bool tenantScoped = false, string? tenantId = null)
    {
        using var activity = Telemetry.Source.StartActivity("db.execute", ActivityKind.Client);
        activity?.SetTag("db.system", "postgresql");
        activity?.SetTag("db.statement", sql);

        logger.LogDebug("Executing command: {Sql}", sql);

        try
        {
            int rows;
            if (tenantScoped)
            {
                rows = await RunTenantScopedAsync(
                    tenantId, (conn, tx) => conn.ExecuteAsync(sql, param, tx));
            }
            else
            {
                await using var conn = CreateConnection();
                rows = await conn.ExecuteAsync(sql, param);
            }
            activity?.SetTag("db.rows_affected", rows);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return rows;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.RecordException(ex);
            throw;
        }
    }

    public async Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? param = null, bool tenantScoped = false, string? tenantId = null)
    {
        using var activity = Telemetry.Source.StartActivity("db.query_single", ActivityKind.Client);
        activity?.SetTag("db.system", "postgresql");
        activity?.SetTag("db.statement", sql);

        try
        {
            T? result;
            if (tenantScoped)
            {
                result = await RunTenantScopedAsync(
                    tenantId, (conn, tx) => conn.QuerySingleOrDefaultAsync<T>(sql, param, tx));
            }
            else
            {
                await using var conn = CreateConnection();
                result = await conn.QuerySingleOrDefaultAsync<T>(sql, param);
            }
            activity?.SetStatus(ActivityStatusCode.Ok);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.RecordException(ex);
            throw;
        }
    }

    /// <summary>
    /// Opens a fresh connection, switches to the non-superuser <c>iverson_runtime</c> role for the
    /// duration of one transaction, and sets the RLS session GUC to <paramref name="tenantId"/>
    /// (which may be <c>null</c> — that flows into <c>set_config</c> as a NULL session value, so
    /// RLS's <c>current_setting(..., true) = tenant_col</c> predicate fails closed to zero rows
    /// rather than falling back to an unfiltered read on the superuser <c>iverson</c> role).
    /// </summary>
    private async Task<T> RunTenantScopedAsync<T>(string? tenantId, Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> statement)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            await conn.ExecuteAsync("SET LOCAL ROLE iverson_runtime", null, tx);
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @TenantId, true)", new { TenantId = tenantId }, tx);
            var result = await statement(conn, tx);
            await tx.CommitAsync();
            return result;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    private sealed class TransactionContext(NpgsqlConnection conn, NpgsqlTransaction tx) : IDbTransactionContext
    {
        public Task<int> ExecuteAsync(string sql, object? param = null) =>
            conn.ExecuteAsync(sql, param, tx);

        public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? param = null) =>
            conn.QuerySingleOrDefaultAsync<T>(sql, param, tx);
    }

    public async Task ExecuteInTransactionAsync(Func<IDbTransactionContext, Task> work)
    {
        await ExecuteInTransactionAsync(async ctx => { await work(ctx); return true; });
    }

    public async Task<T> ExecuteInTransactionAsync<T>(Func<IDbTransactionContext, Task<T>> work)
    {
        using var activity = Telemetry.Source.StartActivity("db.transaction", ActivityKind.Client);
        activity?.SetTag("db.system", "postgresql");

        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        try
        {
            var result = await work(new TransactionContext(conn, tx));
            await tx.CommitAsync();
            activity?.SetStatus(ActivityStatusCode.Ok);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.RecordException(ex);
            try
            {
                await tx.RollbackAsync();
            }
            catch (Exception rollbackEx)
            {
                // A broken connection (e.g. dropped mid-transaction) can make RollbackAsync
                // itself throw. That must never replace the original exception — it's the
                // one that explains what actually went wrong — so log the rollback failure
                // and let the original exception propagate via `throw;` below.
                logger.LogError(rollbackEx, "Rollback failed after transaction error");
            }
            throw;
        }
    }

}
