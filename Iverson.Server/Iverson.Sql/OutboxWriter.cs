namespace Iverson.Sql;

public interface IOutboxWriter
{
    Task<Guid> UpsertAndEnqueueOutboxAsync(TableSchema schema, string typeName, string key, string payloadJson, string? tenantId = null);
    Task DeleteOutboxRowIfPresentAsync(Guid outboxRowId);
    Task EnqueueDeleteOutboxRowAsync(IDbTransactionContext tx, Guid id, string typeName, string key, string payload);
}

public sealed class OutboxWriter(
    string outboxTableName,
    IRecordStoreQueryExecutor sql,
    IRecordStoreTransactionRunner txRunner) : IOutboxWriter
{
    public async Task<Guid> UpsertAndEnqueueOutboxAsync(
        TableSchema schema, string typeName, string key, string payloadJson, string? tenantId = null)
    {
        var allCols   = schema.Columns.Select(c => c.Name).ToList();
        var updateSet = allCols.Count > 0
            ? string.Join(", ", allCols.Select(c => $"\"{c}\" = EXCLUDED.\"{c}\""))
            : $"\"{schema.KeyColumn.Name}\" = EXCLUDED.\"{schema.KeyColumn.Name}\"";

        var upsertSql =
            $"""
            INSERT INTO "{schema.TableName}"
            SELECT * FROM json_populate_record(null::"{schema.TableName}", @Json::json)
            ON CONFLICT ("{schema.KeyColumn.Name}") DO UPDATE SET {updateSet}
            """;

        var outboxSql =
            $"""
            INSERT INTO "{outboxTableName}"
                ("Id", "TypeName", "EntityKey", "EnqueuedAt", "Attempts", "LastError", "LastAttemptAt")
            VALUES
                (@Id, @TypeName, @EntityKey, @EnqueuedAt, 0, null, null)
            """;

        var outboxRowId = Guid.CreateVersion7();

        await txRunner.ExecuteInTransactionAsync(async tx =>
        {
            if (tenantId is not null)
            {
                await tx.ExecuteAsync("SET LOCAL ROLE iverson_runtime");
                await tx.ExecuteAsync("SELECT set_config('app.tenant_id', @TenantId, true)", new { TenantId = tenantId });
            }
            await tx.ExecuteAsync(upsertSql, new { Json = payloadJson });
            if (tenantId is not null)
                await tx.ExecuteAsync("RESET ROLE");
            await tx.ExecuteAsync(outboxSql, new
            {
                Id = outboxRowId,
                TypeName = typeName,
                EntityKey = key,
                EnqueuedAt = DateTimeOffset.UtcNow
            });
        });

        return outboxRowId;
    }

    public Task DeleteOutboxRowIfPresentAsync(Guid outboxRowId) =>
        sql.ExecuteAsync(
            $"""
            DELETE FROM "{outboxTableName}"
            WHERE "Id" = @Id
            """,
            new { Id = outboxRowId });

    public Task EnqueueDeleteOutboxRowAsync(
        IDbTransactionContext tx, Guid id, string typeName, string key, string payload) =>
        tx.ExecuteAsync(
            $"""
            INSERT INTO "{outboxTableName}"
                ("Id", "TypeName", "EntityKey", "EnqueuedAt", "Attempts", "LastError", "LastAttemptAt", "EventType", "Payload")
            VALUES
                (@Id, @TypeName, @EntityKey, @EnqueuedAt, 0, null, null, 'Deleted', @Payload)
            """,
            new
            {
                Id = id,
                TypeName = typeName,
                EntityKey = key,
                EnqueuedAt = DateTimeOffset.UtcNow,
                Payload = payload
            });
}
