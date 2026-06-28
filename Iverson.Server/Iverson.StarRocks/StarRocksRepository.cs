using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Iverson.StarRocks;

public sealed class StarRocksRepository(string connectionString, ILogger<StarRocksRepository> logger)
    : IStarRocksRepository
{
    private readonly string _dbName = new MySqlConnectionStringBuilder(connectionString).Database;

    private MySqlConnection CreateConnection() => new(connectionString);

    public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null)
    {
        using var activity = Telemetry.Source.StartActivity("sr.query", ActivityKind.Client);
        activity?.SetTag("db.system", "starrocks");
        activity?.SetTag("db.statement", sql);

        await using var conn = CreateConnection();
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
        using var activity = Telemetry.Source.StartActivity("sr.execute", ActivityKind.Client);
        activity?.SetTag("db.system", "starrocks");
        activity?.SetTag("db.statement", sql);

        await using var conn = CreateConnection();
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

    public async Task UpsertAsync(StarRocksTableSchema schema, string payloadJson)
    {
        using var activity = Telemetry.Source.StartActivity("sr.upsert", ActivityKind.Client);
        activity?.SetTag("db.system", "starrocks");
        activity?.SetTag("db.table", schema.TableName);

        var row = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payloadJson)
            ?? new Dictionary<string, JsonElement>();

        var knownCols = schema.Columns
            .Select(c => c.Name)
            .Append(schema.KeyColumn.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var entries = row
            .Where(kv => knownCols.Contains(kv.Key)
                      && kv.Value.ValueKind != JsonValueKind.Object
                      && kv.Value.ValueKind != JsonValueKind.Undefined)
            .ToList();

        if (entries.Count == 0) return;

        var colList   = string.Join(", ", entries.Select(e => $"`{e.Key}`"));
        var paramList = string.Join(", ", entries.Select((_, i) => $"@p{i}"));

        // StarRocks Primary Key model treats INSERT of an existing key as a FULL-ROW REPLACE:
        // any column absent from the INSERT list is reset to its default/null.
        // This is safe here because both ObjectPersistenceGrpcService.Update and
        // ObjectMappingGrpcService.Update call StructSerializer.SerializePayload on
        // request.Payload — which serialises the ENTIRE Struct the client sent —
        // and the API contract requires clients to supply the complete entity on Update.
        // If a partial-payload Update is ever introduced the producer must be changed first.
        var sql       = $"INSERT INTO `{schema.TableName}` ({colList}) VALUES ({paramList})";

        var param = new DynamicParameters();
        for (var i = 0; i < entries.Count; i++)
            param.Add($"p{i}", JsonElementToObject(entries[i].Value));

        await ExecuteAsync(sql, param);
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    public Task DeleteAsync(string tableName, string keyColumn, string keyValue) =>
        ExecuteAsync(
            $"DELETE FROM `{tableName}` WHERE `{keyColumn}` = @key",
            new { key = keyValue });

    internal static string BuildCreateTableDdl(StarRocksTableSchema schema)
    {
        var keySql  = $"`{schema.KeyColumn.Name}` {schema.KeyColumn.SrType} NOT NULL";
        var colsSql = schema.Columns.Select(c =>
            $"`{c.Name}` {c.SrType}{(c.IsNullable ? "" : " NOT NULL")}");

        var orderBy = schema.MvSortKey.Count > 0
            ? $"\nORDER BY ({string.Join(", ", schema.MvSortKey.Select(k => $"`{k}`"))})"
            : "";

        return $"""
            CREATE TABLE IF NOT EXISTS `{schema.TableName}` (
                {keySql},
                {string.Join(",\n    ", colsSql)}
            ) ENGINE=OLAP
            UNIQUE KEY(`{schema.KeyColumn.Name}`){orderBy}
            DISTRIBUTED BY HASH(`{schema.KeyColumn.Name}`) BUCKETS 4
            PROPERTIES ("replication_num" = "1")
            """;
    }

    public async Task ApplyTableAsync(StarRocksTableSchema schema)
    {
        using var activity = Telemetry.Source.StartActivity("sr.apply_table", ActivityKind.Client);
        activity?.SetTag("db.system", "starrocks");
        activity?.SetTag("db.table", schema.TableName);

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

        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    public async Task<bool> IsHealthyAsync()
    {
        try
        {
            var result = await QueryAsync<int>("SELECT 1");
            return result.Any();
        }
        catch
        {
            return false;
        }
    }

    private async Task EnsureDatabaseAsync()
    {
        var builder = new MySqlConnectionStringBuilder(connectionString) { Database = string.Empty };
        await using var conn = new MySqlConnection(builder.ToString());
        await conn.ExecuteAsync($"CREATE DATABASE IF NOT EXISTS `{_dbName}`");
    }

    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? (object)l : el.GetDouble(),
        JsonValueKind.True   => true,
        JsonValueKind.False  => false,
        JsonValueKind.Null   => null,
        JsonValueKind.Array  => el.GetRawText(),
        _                    => el.GetRawText()
    };
}
