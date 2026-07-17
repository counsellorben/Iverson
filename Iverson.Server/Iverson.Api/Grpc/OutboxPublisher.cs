using System.Diagnostics;
using Iverson.Events;
using Iverson.Sql;

namespace Iverson.Api.Grpc;

public interface IOutboxPublisher
{
    Task PublishAsync(
        EntityEventType eventType,
        string typeName,
        string key,
        string payloadJson,
        string? requestTraceId,
        StoreTarget targetStores,
        Guid outboxRowId,
        string opLabel,
        CancellationToken ct = default);
}

public sealed class OutboxPublisher(
    IEventProducer events, IOutboxWriter outboxWriter, ILogger<OutboxPublisher> logger)
    : IOutboxPublisher
{
    private const string SchemaVersion = "1";

    public async Task PublishAsync(
        EntityEventType eventType, string typeName, string key, string payloadJson,
        string? requestTraceId, StoreTarget targetStores,
        Guid outboxRowId, string opLabel, CancellationToken ct = default)
    {
        var traceId = requestTraceId.NullIfEmpty() ?? Activity.Current?.TraceId.ToString() ?? string.Empty;
        var published = false;
        try
        {
            await events.ProduceAsync(EntityTopics.Events, key,
                new EntityEvent(eventType, typeName, key, payloadJson, traceId, SchemaVersion, DateTimeOffset.UtcNow, targetStores));
            published = true;
            await outboxWriter.DeleteOutboxRowIfPresentAsync(outboxRowId);
        }
        catch (Exception ex) when (!published)
        {
            logger.LogWarning(ex, "[{Op}] Opportunistic publish failed for type={Type} key={Key} — ReconciliationQueueWorker will retry from the durable outbox row",
                opLabel, typeName.SanitizeForLog(), key);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[{Op}] Publish succeeded but outbox cleanup failed for type={Type} key={Key} — ReconciliationQueueWorker will harmlessly re-publish from the durable outbox row",
                opLabel, typeName.SanitizeForLog(), key);
        }
    }
}

file static class StringExtensions
{
    internal static string? NullIfEmpty(this string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;
}
