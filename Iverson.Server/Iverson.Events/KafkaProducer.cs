using System.Diagnostics;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace Iverson.Events;

public class KafkaProducer(
    IProducer<string, string> producer,
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

    public void Dispose()
    {
        producer.Flush(TimeSpan.FromSeconds(10));
        producer.Dispose();
    }
}
