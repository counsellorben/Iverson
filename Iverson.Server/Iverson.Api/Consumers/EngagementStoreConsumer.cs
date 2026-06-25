using System.Diagnostics;
using System.Text.Json;
using Iverson.Api.Schema;
using Iverson.Elasticsearch;
using Iverson.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Iverson.Api.Consumers;

/// <summary>
/// Subscribes to entity events and keeps Elasticsearch in sync.
/// Handles Engagement flag — index this entity's own document.
/// </summary>
public sealed class EngagementStoreConsumer(
    IEventConsumer consumer,
    IElasticsearchService es,
    SchemaRegistry registry,
    ILogger<EngagementStoreConsumer> logger) : BackgroundService
{
    private const string GroupId = "iverson.consumer.engagement";

    protected override Task ExecuteAsync(CancellationToken ct) =>
        Task.WhenAll(
            consumer.ConsumeAsync(EntityTopics.Created, GroupId, HandleUpsertAsync, ct),
            consumer.ConsumeAsync(EntityTopics.Updated, GroupId, HandleUpsertAsync, ct),
            consumer.ConsumeAsync(EntityTopics.Deleted, GroupId + ".delete", HandleDeleteAsync, ct));

    internal async Task HandleUpsertAsync(string key, string value, CancellationToken ct)
    {
        var ev = Deserialize(key, value);
        if (ev is null) return;

        // Direct index: entity has all required relations resolved
        if (ev.TargetStores.HasFlag(StoreTarget.Engagement))
        {
            var schema = registry.Get(ev.TypeName);
            if (schema is null)
            {
                logger.LogError(
                    "[Engagement] Dropped event — no schema registered for type={Type} key={Key}.",
                    ev.TypeName, ev.Key);
                Activity.Current?.SetTag("dropped_event.direct_index", true)
                                 .SetTag("dropped_event.reason", "schema_not_found")
                                 .SetTag("dropped_event.type", ev.TypeName);
            }
            else
            {
                await IndexDocumentAsync(schema.TableName, ev.Key, ev.PayloadJson);
            }
        }
    }

    internal async Task HandleDeleteAsync(string key, string value, CancellationToken ct)
    {
        var ev = Deserialize(key, value);
        if (ev is null) return;

        if (!ev.TargetStores.HasFlag(StoreTarget.Engagement)) return;

        var schema = registry.Get(ev.TypeName);
        if (schema is null)
        {
            logger.LogError(
                "[Engagement] Dropped event — no schema registered for type={Type} key={Key}.",
                ev.TypeName, ev.Key);
            Activity.Current?.SetTag("dropped_event", true)
                             .SetTag("dropped_event.reason", "schema_not_found")
                             .SetTag("dropped_event.type", ev.TypeName);
            return;
        }

        await es.DeleteDocumentAsync(schema.TableName, ev.Key);
        logger.LogInformation("[Engagement] Deleted {Type}:{Key}", ev.TypeName, ev.Key);
    }

    private async Task IndexDocumentAsync(string indexName, string docKey, string payloadJson)
    {
        var doc = JsonSerializer.Deserialize<Dictionary<string, object>>(payloadJson, s_jsonOptions);
        if (doc is null) return;
        await es.IndexDocumentAsync(indexName, docKey, doc);
    }

    private EntityEvent? Deserialize(string key, string value)
    {
        try
        {
            return JsonSerializer.Deserialize<EntityEvent>(value, s_jsonOptions);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Engagement] Failed to deserialize event key={Key}", key);
            return null;
        }
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}
