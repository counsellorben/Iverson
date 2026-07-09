using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Polly;
using Polly.CircuitBreaker;

namespace Iverson.StarRocks;

public sealed class StarRocksSchemaManager(
    string connectionString,
    ILogger<StarRocksSchemaManager> logger,
    StarRocksResilienceOptions? resilienceOptions = null)
    : IStarRocksSchemaManager
{
    private readonly string _dbName = new MySqlConnectionStringBuilder(connectionString).Database;

    private readonly StarRocksReadinessGate _readinessGate = new(
        ct => CheckBackendAliveAsync(connectionString, ct),
        (resilienceOptions ?? StarRocksResilienceOptions.Default).BackendReadyTimeout);

    private readonly ResiliencePipeline _pipeline =
        StarRocksResiliencePipelineFactory.Build(
            (resilienceOptions ?? StarRocksResilienceOptions.Default).CircuitBreaker, logger);

    private MySqlConnection CreateConnection() => new(connectionString);

    private async Task RunAsync(Func<Task> operation)
    {
        await _readinessGate.EnsureReadyAsync().ConfigureAwait(false);

        try
        {
            await _pipeline
                .ExecuteAsync(async _ => { await operation().ConfigureAwait(false); return true; })
                .ConfigureAwait(false);
        }
        catch (BrokenCircuitException ex)
        {
            throw new StarRocksNotReadyException(
                "StarRocks is currently unavailable (circuit breaker open).", ex);
        }
    }

    internal static string BuildCreateTableDdl(StarRocksTableSchema schema)
    {
        var keySql  = $"`{schema.KeyColumn.Name}` {schema.KeyColumn.SrType} NOT NULL";
        var colsSql = schema.Columns.Select(c =>
            $"`{c.Name}` {c.SrType}{(c.IsNullable ? "" : " NOT NULL")}");

        var orderBy = schema.SortKey.Count > 0
            ? $"\nORDER BY ({string.Join(", ", schema.SortKey.Select(k => $"`{k}`"))})"
            : "";

        return $"""
            CREATE TABLE IF NOT EXISTS `{schema.TableName}` (
                {keySql},
                {string.Join(",\n    ", colsSql)}
            ) ENGINE=OLAP
            PRIMARY KEY(`{schema.KeyColumn.Name}`)
            DISTRIBUTED BY HASH(`{schema.KeyColumn.Name}`) BUCKETS 4{orderBy}
            PROPERTIES ("replication_num" = "1")
            """;
    }

    public async Task ApplyTableAsync(StarRocksTableSchema schema)
    {
        using var activity = Telemetry.Source.StartActivity("sr.apply_table", ActivityKind.Client);
        activity?.SetTag("db.system", "starrocks");
        activity?.SetTag("db.table", schema.TableName);

        await RunAsync(async () =>
        {
            await EnsureDatabaseAsync();

            await using var conn = CreateConnection();

            var exists = await conn.QuerySingleOrDefaultAsync<int>(
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = @db AND table_name = @tbl",
                new { db = _dbName, tbl = schema.TableName });

            if (exists == 0)
            {
                await conn.ExecuteAsync(BuildCreateTableDdl(schema));
                logger.LogInformation("Created StarRocks table {Table}", schema.TableName);
            }
            else
            {
                // Only ADDS columns that are missing. Type widening, column removal, and
                // primary-key changes are not handled — those require manual DDL migration.
                var existingCols = (await conn.QueryAsync<string>(
                    "SELECT column_name FROM information_schema.columns WHERE table_schema = @db AND table_name = @tbl",
                    new { db = _dbName, tbl = schema.TableName }
                )).ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var col in schema.Columns.Where(c => !existingCols.Contains(c.Name)))
                {
                    await conn.ExecuteAsync(
                        $"ALTER TABLE `{schema.TableName}` ADD COLUMN `{col.Name}` {col.SrType}");
                    logger.LogInformation("Added column {Col} to StarRocks table {Table}", col.Name, schema.TableName);
                }
            }
        });

        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    private async Task EnsureDatabaseAsync()
    {
        var builder = new MySqlConnectionStringBuilder(connectionString) { Database = string.Empty };
        await using var conn = new MySqlConnection(builder.ToString());
        await conn.ExecuteAsync($"CREATE DATABASE IF NOT EXISTS `{_dbName}`");
    }

    private static async Task<bool> CheckBackendAliveAsync(string connectionString, CancellationToken ct)
    {
        var probeConnectionString = new MySqlConnectionStringBuilder(connectionString) { Database = "" }.ToString();
        await using var conn = new MySqlConnection(probeConnectionString);
        await conn.OpenAsync(ct);
        return await StarRocksHealthChecker.AnyBackendAliveAsync(conn, ct);
    }
}
