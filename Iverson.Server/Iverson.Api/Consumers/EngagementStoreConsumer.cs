using System.Text.Json;
using Iverson.Api.Schema;
using Iverson.Elasticsearch;
using Iverson.Events;
using Iverson.Sql;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Iverson.Api.Consumers;

/// <summary>
/// Subscribes to entity events and keeps Elasticsearch in sync.
/// Handles two flag cases:
///   Engagement       — index this entity's own document.
///   EngagementFanout — re-index all ES-eligible documents that embed this entity as a relation.
/// </summary>
public sealed class EngagementStoreConsumer(
    IEventConsumer consumer,
    IElasticsearchService es,
    IPostgresRepository sql,
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
            if (schema is not null)
                await IndexDocumentAsync(schema.IndexName, ev.Key, ev.PayloadJson);
        }

        // Fan-out: re-index dependent documents that embed this entity
        if (ev.TargetStores.HasFlag(StoreTarget.EngagementFanout))
            await FanOutAsync(ev, ct);
    }

    internal async Task HandleDeleteAsync(string key, string value, CancellationToken ct)
    {
        var ev = Deserialize(key, value);
        if (ev is null) return;

        if (!ev.TargetStores.HasFlag(StoreTarget.Engagement) &&
            !ev.TargetStores.HasFlag(StoreTarget.EngagementFanout)) return;

        var schema = registry.Get(ev.TypeName);
        if (schema is null) return;

        await es.DeleteDocumentAsync(schema.IndexName, ev.Key);
        logger.LogInformation("[Engagement] Deleted {Type}:{Key}", ev.TypeName, ev.Key);
    }

    private async Task FanOutAsync(EntityEvent ev, CancellationToken ct)
    {
        var dependents = registry.GetDirectEngagementDependents(ev.TypeName);
        if (dependents.Count == 0) return;

        foreach (var depSchema in dependents)
        {
            // Find the relation that links from depSchema to the changed entity
            var relation = depSchema.Relations.FirstOrDefault(r =>
                string.Equals(r.RelatedTypeName, ev.TypeName, StringComparison.OrdinalIgnoreCase));

            if (relation is null) continue;

            try
            {
                string querySql;

                // ManyToMany: FK is an array column (e.g. TagIds UUID[])
                if (relation.ForeignKey.EndsWith("Ids", StringComparison.OrdinalIgnoreCase))
                {
                    querySql = $"""
                        SELECT row_to_json(t)::text
                        FROM "{depSchema.TableName}" t
                        WHERE @Key::uuid = ANY("{relation.ForeignKey}")
                        """;
                }
                else
                {
                    querySql = $"""
                        SELECT row_to_json(t)::text
                        FROM "{depSchema.TableName}" t
                        WHERE "{relation.ForeignKey}" = @Key::uuid
                        """;
                }

                var rows = await sql.QueryAsync<string>(querySql, new { Key = ev.Key });

                foreach (var rowJson in rows)
                {
                    // Extract the primary key from the row JSON
                    using var doc    = JsonDocument.Parse(rowJson);
                    var depKeyValue  = FindKey(doc.RootElement, depSchema.KeyColumn.Name);
                    if (depKeyValue is null) continue;

                    await IndexDocumentAsync(depSchema.IndexName, depKeyValue, rowJson);
                }

                logger.LogInformation("[Engagement] Fan-out {DepType} for {SrcType}:{Key}",
                    depSchema.TypeName, ev.TypeName, ev.Key);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Engagement] Fan-out failed for {DepType} triggered by {SrcType}:{Key}",
                    depSchema.TypeName, ev.TypeName, ev.Key);
            }
        }
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

    // Postgres row_to_json uses lowercase column names; try both casings.
    private static string? FindKey(JsonElement root, string keyName)
    {
        if (root.TryGetProperty(keyName, out var v)) return v.ToString();
        if (root.TryGetProperty(keyName.ToLowerInvariant(), out var vl)) return vl.ToString();
        return null;
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}
