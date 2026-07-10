using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Iverson.Sql;

public class PostgresRepository(
    string connectionString,
    ILogger<PostgresRepository> logger) : IPostgresQueryExecutor, IPostgresTransactionRunner
{
    private NpgsqlConnection CreateConnection() => new(connectionString);

    public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null)
    {
        using var activity = Telemetry.Source.StartActivity("db.query", ActivityKind.Client);
        activity?.SetTag("db.system", "postgresql");
        activity?.SetTag("db.statement", sql);

        await using var conn = CreateConnection();
        logger.LogDebug("Executing query: {Sql}", sql);

        try
        {
            var results = await conn.QueryAsync<T>(sql, param);
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

    public async Task<int> ExecuteAsync(string sql, object? param = null)
    {
        using var activity = Telemetry.Source.StartActivity("db.execute", ActivityKind.Client);
        activity?.SetTag("db.system", "postgresql");
        activity?.SetTag("db.statement", sql);

        await using var conn = CreateConnection();
        logger.LogDebug("Executing command: {Sql}", sql);

        try
        {
            var rows = await conn.ExecuteAsync(sql, param);
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

    public async Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? param = null)
    {
        using var activity = Telemetry.Source.StartActivity("db.query_single", ActivityKind.Client);
        activity?.SetTag("db.system", "postgresql");
        activity?.SetTag("db.statement", sql);

        await using var conn = CreateConnection();

        try
        {
            var result = await conn.QuerySingleOrDefaultAsync<T>(sql, param);
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
