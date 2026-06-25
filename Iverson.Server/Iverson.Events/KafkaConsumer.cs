using System.Diagnostics;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging;

namespace Iverson.Events;

public class KafkaConsumer(
    string bootstrapServers,
    ILogger<KafkaConsumer> logger,
    MessageDispatcher dispatcher) : IEventConsumer
{
    public async Task ConsumeAsync(string topic, string groupId, Func<string, string, CancellationToken, Task> handler, CancellationToken cancellationToken)
    {
        await EnsureTopicExistsAsync(topic, cancellationToken);

        var config = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = groupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(topic);
        logger.LogInformation("Subscribed to topic {Topic} as group {GroupId}", topic, groupId);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = consumer.Consume(cancellationToken);
                if (result is null) continue;

                ActivityContext parentContext = ExtractTraceContext(result.Message.Headers);

                using var activity = Telemetry.Source.StartActivity(
                    "kafka.consume",
                    ActivityKind.Consumer,
                    parentContext);

                activity?.SetTag("messaging.system", "kafka");
                activity?.SetTag("messaging.source", topic);
                activity?.SetTag("messaging.source_kind", "topic");
                activity?.SetTag("messaging.consumer_group", groupId);
                activity?.SetTag("messaging.message_id", result.Message.Key);
                activity?.SetTag("messaging.kafka.partition", result.Partition.Value);
                activity?.SetTag("messaging.kafka.offset", result.Offset.Value);
                activity?.SetTag("messaging.operation", "receive");

                var ctx = new DispatchContext(
                    topic, groupId, result.Message.Key, result.Message.Value, result.Message.Headers);

                try
                {
                    await dispatcher.DispatchAsync(ctx, handler, cancellationToken);
                    consumer.Commit(result);
                    activity?.SetStatus(ActivityStatusCode.Ok);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // The DLQ write itself failed — do not commit. Halt this consumer
                    // so we never advance past an uncommitted message (block rather than lose).
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    activity?.RecordException(ex);
                    logger.LogCritical(ex,
                        "[Consumer] Halting topic={Topic} group={Group} — offset not committed", topic, groupId);
                    throw;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ConsumeException ex)
            {
                logger.LogError(ex, "Consume error on topic {Topic}", topic);
                await Task.Delay(1000, cancellationToken);
            }
        }

        consumer.Close();
    }

    private async Task EnsureTopicExistsAsync(string topic, CancellationToken ct)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrapServers }).Build();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await admin.CreateTopicsAsync([new TopicSpecification { Name = topic, NumPartitions = 1, ReplicationFactor = 1 }]);
                logger.LogInformation("Created Kafka topic {Topic}", topic);
                return;
            }
            catch (CreateTopicsException ex) when (ex.Results.All(r => r.Error.Code == ErrorCode.TopicAlreadyExists))
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Kafka not ready for topic {Topic}, retrying: {Message}", topic, ex.Message);
                await Task.Delay(2000, ct);
            }
        }
    }

    private static ActivityContext ExtractTraceContext(Headers headers)
    {
        var traceparentBytes = headers.FirstOrDefault(h => h.Key == "traceparent")?.GetValueBytes();
        if (traceparentBytes is null) return default;

        try
        {
            var traceparent = System.Text.Encoding.UTF8.GetString(traceparentBytes);
            var parts = traceparent.Split('-');
            if (parts.Length < 4) return default;

            var traceId = ActivityTraceId.CreateFromString(parts[1].AsSpan());
            var spanId = ActivitySpanId.CreateFromString(parts[2].AsSpan());
            var flags = parts[3] == "01" ? ActivityTraceFlags.Recorded : ActivityTraceFlags.None;

            return new ActivityContext(traceId, spanId, flags, isRemote: true);
        }
        catch
        {
            return default;
        }
    }
}
