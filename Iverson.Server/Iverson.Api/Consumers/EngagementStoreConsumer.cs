using System.Text.Json;
using System.Text.Json.Nodes;
using Iverson.Api.Schema;
using Iverson.Events;
using Iverson.Sql;
using Iverson.StarRocks;

namespace Iverson.Api.Consumers;

public sealed class EngagementStoreConsumer(
    IEventConsumer consumer,
    IEngagementStoreEntityStore sr,
    SchemaRegistry registry,
    IEntityRepository entities,
    ILogger<EngagementStoreConsumer> logger) : BackgroundService
{
    private const string GroupId = "iverson.consumer.engagement";

    protected override Task ExecuteAsync(CancellationToken ct) =>
        ConsumerResilience.RunWithRestartAsync(
            () => consumer.ConsumeAsync(EntityTopics.Events, GroupId, DispatchAsync, ct),
            logger,
            "Engagement",
            ct);

    internal async Task DispatchAsync(string key, string value, CancellationToken ct)
    {
        var ev = Deserialize(key, value);
        switch (ev.EventType)
        {
            case EntityEventType.Created:
            case EntityEventType.Updated:
                await HandleUpsertAsync(key, value, ct);
                break;
            case EntityEventType.Deleted:
                await HandleDeleteAsync(key, value, ct);
                break;
        }
    }

    internal async Task HandleUpsertAsync(string key, string value, CancellationToken ct)
    {
        var ev = Deserialize(key, value);
        if (!ev.TargetStores.HasFlag(StoreTarget.Engagement)) return;

        var schema = registry.Get(ev.TypeName);
        if (schema is null)
        {
            logger.LogError("[Engagement] Dropped upsert — no schema for type={Type} key={Key}", ev.TypeName, key);
            return;
        }

        // Re-derive the ownership value from the authoritative Postgres row rather than trusting
        // the event payload's own value for it — the payload is unsigned JSON and this value
        // feeds StarRocks's read-time row authorization filtering (CSR #7, StarRocks sibling).
        var ownerField = schema.Authorization?.OwnerField;
        var payloadJson = ev.PayloadJson;
        if (ownerField is not null)
        {
            var authoritativeOwnerValue = await FetchAuthoritativeOwnerValueAsync(schema, ownerField, ev.Key, ct);
            try
            {
                payloadJson = WithOwnerValue(ev.PayloadJson, ownerField, authoritativeOwnerValue);
            }
            catch (JsonException ex)
            {
                throw new PoisonMessageException(
                    $"[Engagement] Malformed payload JSON type={ev.TypeName} key={key}", ex);
            }
        }

        var srSchema = SchemaBuilder.ToStarRocksTableSchema(schema);
        await sr.UpsertAsync(srSchema, payloadJson);
        logger.LogInformation("[Engagement] Upserted {Type}:{Key}", ev.TypeName, key);
    }

    internal async Task HandleDeleteAsync(string key, string value, CancellationToken ct)
    {
        var ev = Deserialize(key, value);
        if (!ev.TargetStores.HasFlag(StoreTarget.Engagement)) return;

        var schema = registry.Get(ev.TypeName);
        if (schema is null)
        {
            logger.LogError("[Engagement] Dropped delete — no schema for type={Type} key={Key}", ev.TypeName, key);
            return;
        }

        await sr.DeleteAsync(schema.TableName, schema.KeyColumn.Name, ev.Key);
        logger.LogInformation("[Engagement] Deleted {Type}:{Key}", ev.TypeName, key);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Re-derives the ownership value from the authoritative Postgres row instead of trusting
    // the event payload's own value for it (CSR #7, StarRocks sibling — event JSON is unsigned
    // and this value feeds StarRocks's read-time row authorization filtering). Fails closed: if
    // the row can't be found (e.g. a delete-then-recreate race), the owner value is treated as
    // absent rather than falling back to the unvalidated payload value.
    private async Task<string?> FetchAuthoritativeOwnerValueAsync(
        SchemaDescriptor schema, string ownerField, string key, CancellationToken ct)
    {
        var rowJson = await entities.FetchByKeyAsync(SchemaBuilder.ToTableSchema(schema), key);
        if (rowJson is null)
        {
            logger.LogWarning(
                "[Engagement] Owner re-derivation found no authoritative row for type={Type} key={Key} — omitting owner value.",
                schema.TypeName.SanitizeForLog(), key);
            return null;
        }

        using var doc = JsonDocument.Parse(rowJson);
        return doc.RootElement.TryGetProperty(ownerField, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString())
            : null;
    }

    // Overrides the owner-column key in a raw event payload JSON document with the authoritative
    // value, or removes the key entirely when the authoritative value is null (fail closed —
    // never fall back to the payload's own, unvalidated value). StarRocks's UpsertAsync takes a
    // whole JSON document rather than per-field values, so unlike the Qdrant/Intelligence sibling
    // fix, the corrected value has to be spliced back into the document rather than assembled
    // into a fresh payload dictionary.
    private static string WithOwnerValue(string payloadJson, string ownerField, string? authoritativeOwnerValue)
    {
        var node = JsonNode.Parse(payloadJson)!.AsObject();
        if (authoritativeOwnerValue is null)
            node.Remove(ownerField);
        else
            node[ownerField] = JsonValue.Create(authoritativeOwnerValue);

        return node.ToJsonString();
    }

    private static EntityEvent Deserialize(string key, string value)
    {
        EntityEvent? ev;
        try
        {
            ev = JsonSerializer.Deserialize<EntityEvent>(value, s_jsonOptions);
        }
        catch (JsonException ex)
        {
            throw new PoisonMessageException($"[Engagement] Malformed event JSON key={key}", ex);
        }

        return ev ?? throw new PoisonMessageException($"[Engagement] Event deserialized to null key={key}");
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}
