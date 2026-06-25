using System.Diagnostics;
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
        var entityEvent = Deserialize(key, value);
        if (!entityEvent.TargetStores.HasFlag(StoreTarget.Record)) return;

        var schema = registry.Get(entityEvent.TypeName);
        if (schema is null)
        {
            logger.LogError(
                "[Record] Dropped event — no schema registered for type={Type} key={Key}. " +
                "Call RegisterSchema before producing events for this type.",
                entityEvent.TypeName, key);
            Activity.Current?.SetTag("dropped_event", true)
                             .SetTag("dropped_event.reason", "schema_not_found")
                             .SetTag("dropped_event.type", entityEvent.TypeName);
            return;
        }

        await UpsertAsync(schema, entityEvent.PayloadJson);
    }

    internal async Task HandleDeleteAsync(string key, string value, CancellationToken ct)
    {
        var entityEvent = Deserialize(key, value);
        if (!entityEvent.TargetStores.HasFlag(StoreTarget.Record)) return;

        var schema = registry.Get(entityEvent.TypeName);
        if (schema is null)
        {
            logger.LogError(
                "[Record] Dropped event — no schema registered for type={Type} key={Key}. " +
                "Call RegisterSchema before producing events for this type.",
                entityEvent.TypeName, key);
            Activity.Current?.SetTag("dropped_event", true)
                             .SetTag("dropped_event.reason", "schema_not_found")
                             .SetTag("dropped_event.type", entityEvent.TypeName);
            return;
        }

        await sql.ExecuteAsync(
            $"DELETE FROM \"{schema.TableName}\" WHERE \"{schema.KeyColumn.Name}\" = @Key::uuid",
            new { Key = entityEvent.Key });

        logger.LogInformation("[Record] Deleted {Type}:{Key}", entityEvent.TypeName, entityEvent.Key);
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

    private static EntityEvent Deserialize(string key, string value)
    {
        EntityEvent? entityEvent;
        try
        {
            entityEvent = JsonSerializer.Deserialize<EntityEvent>(value, s_jsonOptions);
        }
        catch (JsonException ex)
        {
            throw new PoisonMessageException($"[Record] Malformed event JSON key={key}", ex);
        }

        return entityEvent ?? throw new PoisonMessageException($"[Record] Event deserialized to null key={key}");
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}
