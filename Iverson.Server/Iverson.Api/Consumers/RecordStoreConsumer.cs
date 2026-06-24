using System.Text.Json;
using Iverson.Api.Schema;
using Iverson.Events;
using Iverson.Sql;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Iverson.Api.Consumers;

/// <summary>
/// Subscribes to entity.created, entity.updated, and entity.deleted topics and
/// keeps PostgreSQL (the system of record) in sync via idempotent upserts.
/// Only processes events whose TargetStores includes Record.
/// </summary>
public sealed class RecordStoreConsumer(
    IEventConsumer consumer,
    IPostgresRepository sql,
    SchemaRegistry registry,
    ILogger<RecordStoreConsumer> logger) : BackgroundService
{
    private const string GroupId = "iverson.consumer.record";

    protected override Task ExecuteAsync(CancellationToken ct) =>
        Task.WhenAll(
            consumer.ConsumeAsync(EntityTopics.Created, GroupId, HandleAsync, ct),
            consumer.ConsumeAsync(EntityTopics.Updated, GroupId, HandleAsync, ct),
            consumer.ConsumeAsync(EntityTopics.Deleted, GroupId + ".delete", HandleDeleteAsync, ct));

    internal async Task HandleAsync(string key, string value, CancellationToken ct)
    {
        EntityEvent? entityEvent;
        try
        {
            entityEvent = JsonSerializer.Deserialize<EntityEvent>(value, s_jsonOptions);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Record] Failed to deserialize event key={Key}", key);
            return;
        }

        if (entityEvent is null || !entityEvent.TargetStores.HasFlag(StoreTarget.Record)) return;

        var schema = registry.Get(entityEvent.TypeName);
        if (schema is null)
        {
            logger.LogWarning("[Record] No schema for type={Type}", entityEvent.TypeName);
            return;
        }

        try
        {
            await UpsertAsync(schema, entityEvent.PayloadJson);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Record] Upsert failed type={Type} key={Key}", entityEvent.TypeName, key);
        }
    }

    internal async Task HandleDeleteAsync(string key, string value, CancellationToken ct)
    {
        EntityEvent? entityEvent;
        try
        {
            entityEvent = JsonSerializer.Deserialize<EntityEvent>(value, s_jsonOptions);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Record] Failed to deserialize delete event key={Key}", key);
            return;
        }

        if (entityEvent is null || !entityEvent.TargetStores.HasFlag(StoreTarget.Record)) return;

        var schema = registry.Get(entityEvent.TypeName);
        if (schema is null) return;

        try
        {
            await sql.ExecuteAsync(
                $"DELETE FROM \"{schema.TableName}\" WHERE \"{schema.KeyColumn.Name}\" = @Key::uuid",
                new { Key = entityEvent.Key });

            logger.LogInformation("[Record] Deleted {Type}:{Key}", entityEvent.TypeName, entityEvent.Key);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Record] Delete failed type={Type} key={Key}", entityEvent.TypeName, entityEvent.Key);
        }
    }

    private async Task UpsertAsync(SchemaDescriptor schema, string payloadJson)
    {
        // Use Postgres json_populate_record to handle type coercions from JSON values.
        // The payload JSON uses native types (string/number/bool/array) — see ProtoValueToNative.
        var allCols  = schema.ScalarColumns.Select(c => c.Name).ToList();
        var updateSet = allCols.Count > 0
            ? string.Join(", ", allCols.Select(c => $"\"{c}\" = EXCLUDED.\"{c}\""))
            : "\"" + schema.KeyColumn.Name + "\" = EXCLUDED.\"" + schema.KeyColumn.Name + "\"";

        var upsertSql = $"""
            INSERT INTO "{schema.TableName}"
            SELECT * FROM json_populate_record(null::"{schema.TableName}", @Json::json)
            ON CONFLICT ("{schema.KeyColumn.Name}") DO UPDATE SET {updateSet}
            """;

        await sql.ExecuteAsync(upsertSql, new { Json = payloadJson });

        logger.LogInformation("[Record] Upserted {Type}", schema.TypeName);
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}
