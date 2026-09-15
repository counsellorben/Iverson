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

    private readonly HashSet<(string TenantId, string TableName)> _provisioned = [];

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

        var schema = await registry.GetOrReloadAsync(ev.TypeName, ct);
        if (schema is null)
        {
            // Throw, never return: returning completes the handler normally and the caller COMMITS
            // the offset, which loses the write terminally. Throwing hands the message to the
            // dispatcher's bounded retry and then the DLQ, so it is recoverable either way.
            throw new InvalidOperationException(
                $"[Engagement] No schema registered for type '{ev.TypeName}' (key '{key}') after a forced registry reload.");
        }

        var row = await ProjectionTenantResolution.FetchAuthoritativeRowAsync(entities, schema, ev.Key, "[Engagement]");
        if (row is null)
        {
            logger.LogWarning("[Engagement] Dropped upsert — no authoritative tenant value for type={Type} key={Key}", ev.TypeName.SanitizeForLog(), key);
            return;
        }

        var authoritativeTenantValue = row.TenantId;

        var srSchema = SchemaBuilder.ToEngagementTableSchema(schema);

        if (!_provisioned.Contains((authoritativeTenantValue, srSchema.TableName)))
        {
            await sr.EnsureTenantProvisionedAsync(authoritativeTenantValue, srSchema);
            _provisioned.Add((authoritativeTenantValue, srSchema.TableName));
        }

        // Re-derive the ownership value from the authoritative Postgres row rather than trusting
        // the event payload's own value for it — the payload is unsigned JSON and this value
        // feeds StarRocks's read-time row authorization filtering (CSR #7, StarRocks sibling).
        var ownerField = schema.Authorization?.OwnerField;
        var payloadJson = ev.PayloadJson;
        if (ownerField is not null)
        {
            var authoritativeOwnerValue = row.ReadString(ownerField);
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

        await sr.UpsertAsync(srSchema, payloadJson, authoritativeTenantValue);
        logger.LogInformation("[Engagement] Upserted {Type}:{Key}", ev.TypeName.SanitizeForLog(), key);
    }

    internal async Task HandleDeleteAsync(string key, string value, CancellationToken ct)
    {
        var ev = Deserialize(key, value);
        if (!ev.TargetStores.HasFlag(StoreTarget.Engagement)) return;

        var schema = await registry.GetOrReloadAsync(ev.TypeName, ct);
        if (schema is null)
        {
            throw new InvalidOperationException(
                $"[Engagement] No schema registered for type '{ev.TypeName}' (key '{key}') after a forced registry reload.");
        }

        var tenantValue = ProjectionTenantResolution.TenantFromSnapshot(ev.PayloadJson, schema, ev.Key, "[Engagement]");

        await sr.DeleteAsync(schema.TableName, schema.KeyColumn.Name, ev.Key, tenantValue);
        logger.LogInformation("[Engagement] Deleted {Type}:{Key}", ev.TypeName.SanitizeForLog(), key);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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

/// <summary>
/// The one place the Engagement__Enabled gate is expressed. This single gate covers engagement
/// writes and StarRocks DDL, because <c>EnsureTenantProvisionedAsync</c> — the only place
/// StarRocks tables are created — is called solely from this consumer.
/// </summary>
internal static class EngagementStoreRegistration
{
    internal static IServiceCollection AddEngagementStoreConsumer(
        this IServiceCollection services, IConfiguration config, bool isWorker)
    {
        if (isWorker && config.GetValue($"{EngagementStoreOptions.Section}:Enabled", true))
            services.AddHostedService<EngagementStoreConsumer>();

        return services;
    }
}
