using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Iverson.Sql;

public class PostgresRepository(
    string connectionString,
    ILogger<PostgresRepository> logger) : IPostgresRepository
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

    public async Task ApplySchemaAsync(TableSchema schema)
    {
        using var activity = Telemetry.Source.StartActivity("db.apply_schema", ActivityKind.Client);
        activity?.SetTag("db.system", "postgresql");
        activity?.SetTag("db.table", schema.TableName);

        await using var conn = CreateConnection();
        await conn.OpenAsync();

        try
        {
            var existingColumns = (await conn.QueryAsync<string>(
                """
                SELECT column_name
                FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = @TableName
                """,
                new { schema.TableName })).ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (existingColumns.Count == 0)
            {
                // Table does not exist — CREATE
                var keySql   = $"\"{schema.KeyColumn.Name}\" {schema.KeyColumn.SqlType} PRIMARY KEY";
                var colsSql  = schema.Columns.Select(c =>
                    $"\"{c.Name}\" {c.SqlType}{(c.IsNullable ? "" : " NOT NULL")}");

                var ddl = $"""
                    CREATE TABLE IF NOT EXISTS "{schema.TableName}" (
                        {keySql},
                        {string.Join(",\n    ", colsSql)}
                    )
                    """;

                logger.LogInformation("Creating table {Table}", schema.TableName);
                await conn.ExecuteAsync(ddl);
            }
            else
            {
                // Table exists — ADD new columns, DROP removed columns
                foreach (var col in schema.Columns.Where(c => !existingColumns.Contains(c.Name)))
                {
                    var alterSql = $"""
                        ALTER TABLE "{schema.TableName}"
                        ADD COLUMN IF NOT EXISTS "{col.Name}" {col.SqlType}{(col.IsNullable ? "" : $" NOT NULL DEFAULT ('{GetDefaultForType(col.SqlType)}')")}
                        """;

                    logger.LogInformation("Adding column {Column} to {Table}", col.Name, schema.TableName);
                    await conn.ExecuteAsync(alterSql);
                }

                var schemaColumnNames = schema.Columns
                    .Select(c => c.Name)
                    .Append(schema.KeyColumn.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var orphan in existingColumns.Where(c => !schemaColumnNames.Contains(c)))
                {
                    if (logger.IsEnabled(LogLevel.Information))
                        logger.LogInformation("Dropping removed column {Column} from {Table}", orphan, schema.TableName);
                    await conn.ExecuteAsync(
                        $"ALTER TABLE \"{schema.TableName}\" DROP COLUMN IF EXISTS \"{orphan}\"");
                }
            }

            // Ensure BTREE index on each FK column (idempotent via IF NOT EXISTS)
            foreach (var col in schema
                .Columns
                .Where(c =>
                    c.Name.EndsWith("Id", StringComparison.OrdinalIgnoreCase) ||
                    c.Name.EndsWith("Ids", StringComparison.OrdinalIgnoreCase)))
            {
                var idxName = $"ix_{schema.TableName}_{col.Name}".ToLowerInvariant();
                await conn.ExecuteAsync($"""
                    CREATE INDEX IF NOT EXISTS "{idxName}"
                    ON "{schema.TableName}" ("{col.Name}")
                    """);
            }

            activity?.SetStatus(ActivityStatusCode.Ok);
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

    private static string GetDefaultForType(string sqlType) => sqlType.ToUpperInvariant() switch
    {
        var t when t.StartsWith("INT")       => "0",
        var t when t.StartsWith("FLOAT")     => "0",
        var t when t.StartsWith("REAL")      => "0",
        var t when t.StartsWith("DOUBLE")    => "0",
        var t when t.StartsWith("BOOL")      => "false",
        var t when t.StartsWith("UUID")      => "00000000-0000-0000-0000-000000000000",
        var t when t.StartsWith("TIMESTAMP") => "1970-01-01 00:00:00+00",
        _                                    => ""
    };
}
