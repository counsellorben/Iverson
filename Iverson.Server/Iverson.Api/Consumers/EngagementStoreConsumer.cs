using System.Text.Json;
using Iverson.Api.Schema;
using Iverson.Events;
using Iverson.StarRocks;

namespace Iverson.Api.Consumers;

public sealed class EngagementStoreConsumer(
    IEventConsumer consumer,
    IStarRocksRepository sr,
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
        if (!ev.TargetStores.HasFlag(StoreTarget.Engagement)) return;

        var schema = registry.Get(ev.TypeName);
        if (schema is null)
        {
            logger.LogError("[Engagement] Dropped upsert — no schema for type={Type} key={Key}", ev.TypeName, key);
            return;
        }

        var srSchema = SchemaBuilder.ToStarRocksTableSchema(schema);
        await sr.UpsertAsync(srSchema, ev.PayloadJson);
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
