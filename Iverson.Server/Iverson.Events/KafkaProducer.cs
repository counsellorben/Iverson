using System.Diagnostics;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace Iverson.Events;

public class KafkaProducer(
    IProducer<string, string> producer,
    IFailedPublishSink failedPublishSink,
    ILogger<KafkaProducer> logger) : IEventProducer, IDisposable
{
    public async Task ProduceAsync<T>(string topic, string key, T message) where T : class
    {
        var json = JsonSerializer.Serialize(message);
        await ProduceAsync(topic, key, json);
    }

    public async Task ProduceAsync(string topic, string key, string message)
    {
        using var activity = Telemetry.Source.StartActivity("kafka.produce", ActivityKind.Producer);
        activity?.SetTag("messaging.system", "kafka");
        activity?.SetTag("messaging.destination", topic);
        activity?.SetTag("messaging.destination_kind", "topic");
        activity?.SetTag("messaging.message_id", key);
        activity?.SetTag("messaging.operation", "publish");

        // Propagate trace context via Kafka message headers
        var headers = new Headers();
        if (Activity.Current is { } current)
        {
            headers.Add("traceparent", System.Text.Encoding.UTF8.GetBytes(
                $"00-{current.TraceId}-{current.SpanId}-{(current.ActivityTraceFlags.HasFlag(ActivityTraceFlags.Recorded) ? "01" : "00")}"));
        }

        try
        {
            var result = await producer.ProduceAsync(topic, new Message<string, string>
            {
                Key = key,
                Value = message,
                Headers = headers
            });

            activity?.SetTag("messaging.kafka.partition", result.Partition.Value);
            activity?.SetTag("messaging.kafka.offset", result.Offset.Value);
            activity?.SetStatus(ActivityStatusCode.Ok);
            logger.LogDebug("Produced message to {Topic}[{Partition}]@{Offset}", result.Topic, result.Partition, result.Offset);
        }
        catch (ProduceException<string, string> ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.RecordException(ex);
            logger.LogError(ex, "Failed to produce message to topic {Topic}", topic);
            throw;
        }
    }

    public void PublishFireAndForget<T>(string topic, string typeName, string key, T message) where T : class
    {
        var json = JsonSerializer.Serialize(message);

        var headers = new Headers();
        if (Activity.Current is { } current)
            headers.Add(
                "traceparent",
                System.Text.Encoding.UTF8.GetBytes(
                    $"00-{current.TraceId}-{current.SpanId}-{(current.ActivityTraceFlags.HasFlag(ActivityTraceFlags.Recorded) ? "01" : "00")}"));

        try
        {
            producer.Produce(
                topic,
                new Message<string, string> { Key = key, Value = json, Headers = headers },
                report =>
                {
                    if (report.Error.IsError)
                    {
                        logger.LogError(
                            "Kafka delivery failed topic={Topic} key={Key}: {Error}",
                            topic,
                            key,
                            report.Error.Reason);
                        _ = RecordFailureSafeAsync(typeName, key, report.Error.Reason);
                    }
                });
        }
        catch (KafkaException ex)
        {
            logger.LogError(ex, "Kafka enqueue failed topic={Topic} key={Key}", topic, key);
            _ = RecordFailureSafeAsync(typeName, key, ex.Message);
        }
    }

    private async Task RecordFailureSafeAsync(string typeName, string key, string reason)
    {
        try
        {
            await failedPublishSink.RecordAsync(typeName, key, reason);
        }
        catch (Exception ex)
        {
            // The failure sink write itself failed — this write is now unrecoverable except
            // via the manual /admin/reconcile/{typeName} endpoint. Log loudly; don't throw from
            // a delivery-report callback / fire-and-forget continuation.
            logger.LogCritical(ex,
                "Failed to record reconciliation-queue entry for type={Type} key={Key} reason={Reason}",
                typeName, key, reason);
        }
    }

    public void Dispose()
    {
        producer.Flush(TimeSpan.FromSeconds(10));
        producer.Dispose();
    }
}
