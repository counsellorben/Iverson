using System.Text.Json.Nodes;

namespace Iverson.Sql;

public interface IOutboxWriter
{
    Task<Guid> UpsertAndEnqueueOutboxAsync(TableSchema schema, string typeName, string key, string payloadJson, string? tenantId = null);
    Task DeleteOutboxRowIfPresentAsync(Guid outboxRowId);
    Task EnqueueDeleteOutboxRowAsync(IDbTransactionContext tx, Guid id, string typeName, string key, string payload);
    Task EnqueueUpdateOutboxRowAsync(IDbTransactionContext tx, Guid id, string typeName, string key, string payload);
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

        // The one and only injection point for the server-owned tenant column. Every writer
        // reaches this method, so a future caller cannot bypass it; and because the upsert's
        // ON CONFLICT update set covers every column, a payload arriving here without the column
        // would write NULL over a valid tenant id rather than leave it alone.
        payloadJson = WithTenantColumn(payloadJson, schema.TenantColumn, tenantId);

        await txRunner.ExecuteInTransactionAsync(async tx =>
        {
            if (tenantId is not null)
            {
                await tx.EnterTenantScopeAsync(tenantId);
            }
            await tx.ExecuteAsync(upsertSql, new { Json = payloadJson });
            if (tenantId is not null)
                await tx.ExitTenantScopeAsync();
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

    /// <summary>
    /// Returns <paramref name="payloadJson"/> with <paramref name="tenantColumn"/> set to
    /// <paramref name="tenantId"/>, dropping any client-supplied key that differs from it only by
    /// case. Returns the payload unchanged when the schema declares no tenant column or the caller
    /// passed no tenant value.
    /// <para>
    /// Two properties this must not lose. First, the canonical key casing: the upsert runs through
    /// <c>json_populate_record</c>, which matches column names CASE-SENSITIVELY, so a re-cased key
    /// is silently discarded and the column lands NULL. <see cref="JsonNode"/> round-trips every
    /// other key verbatim, and the tenant key is written from the schema's own spelling. Second,
    /// the server value must WIN over anything the client sent — it derives from the caller's
    /// token, not from client-supplied data — which is why the case-variant removal happens before
    /// the set rather than relying on the assignment to overwrite.
    /// </para>
    /// </summary>
    private static string WithTenantColumn(string payloadJson, string? tenantColumn, string? tenantId)
    {
        if (tenantColumn is null || tenantId is null) return payloadJson;

        if (JsonNode.Parse(payloadJson) is not JsonObject obj) return payloadJson;

        foreach (var key in obj
                     .Select(kv => kv.Key)
                     .Where(k => string.Equals(k, tenantColumn, StringComparison.OrdinalIgnoreCase))
                     .ToList())
            obj.Remove(key);

        obj[tenantColumn] = tenantId;
        return obj.ToJsonString();
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

    public Task EnqueueUpdateOutboxRowAsync(
        IDbTransactionContext tx, Guid id, string typeName, string key, string payload) =>
        tx.ExecuteAsync(
            $"""
            INSERT INTO "{outboxTableName}"
                ("Id", "TypeName", "EntityKey", "EnqueuedAt", "Attempts", "LastError", "LastAttemptAt", "EventType", "Payload")
            VALUES
                (@Id, @TypeName, @EntityKey, @EnqueuedAt, 0, null, null, 'Updated', @Payload)
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
