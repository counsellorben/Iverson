using Confluent.Kafka;
using Iverson.Api.Consumers;
using Iverson.Events;
using Iverson.Sql;

namespace Iverson.Api.Reconciliation;

internal sealed class DlqMonitorConsumer(
    IEventConsumer consumer,
    IDlqRepository dlq,
    ILogger<DlqMonitorConsumer> logger) : BackgroundService
{
    private const string GroupId = "iverson.consumer.dlq-monitor";

    protected override Task ExecuteAsync(CancellationToken ct) =>
        ConsumerResilience.RunWithRestartAsync(
            () => consumer.ConsumeRawAsync(EntityTopics.Dlq, GroupId, HandleAsync, ct),
            logger, "DlqMonitor", ct);

    internal async Task HandleAsync(string key, string value, Headers headers, CancellationToken ct)
    {
        string? Header(string headerKey)
        {
            var bytes = headers.FirstOrDefault(h => h.Key == headerKey)?.GetValueBytes();
            return bytes is null ? null : System.Text.Encoding.UTF8.GetString(bytes);
        }

        var attemptsRaw = Header("dlq.attempts");
        var failedAtRaw = Header("dlq.failed_at");

        await dlq.InsertAsync(new DlqMessage(
            SourceTopic: Header("dlq.source_topic") ?? "",
            ConsumerGroup: Header("dlq.consumer_group") ?? "",
            MessageKey: key,
            MessageValue: value,
            ExceptionType: Header("dlq.exception_type"),
            ExceptionMessage: Header("dlq.exception_message"),
            Attempts: int.TryParse(attemptsRaw, out var a) ? a : 0,
            FailedAt: DateTimeOffset.TryParse(failedAtRaw, out var f) ? f : DateTimeOffset.UtcNow));

        logger.LogInformation("[DlqMonitor] Recorded DLQ message key={Key} sourceTopic={SourceTopic}",
            key, Header("dlq.source_topic"));
    }
}
