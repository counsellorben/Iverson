using System.Globalization;
using System.Text.Json;
using Confluent.Kafka;
using Iverson.Api.Consumers;
using Iverson.Api.Schema;
using Iverson.Events;
using Iverson.Sql;

namespace Iverson.Api.Reconciliation;

internal sealed class DlqMonitorConsumer(
    IEventConsumer consumer,
    IDlqRepository dlq,
    SchemaRegistry registry,
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

        string? tenantId = null;
        EntityEvent? ev = null;
        try
        {
            ev = JsonSerializer.Deserialize<EntityEvent>(value, s_jsonOptions);
        }
        catch (JsonException)
        {
            // Malformed event JSON: record without a tenant scope rather than hot-looping
            // forever on the one message this consumer exists to capture.
        }

        // EntityEvent.TypeName/PayloadJson are non-nullable `string` at compile time, but
        // System.Text.Json does not enforce that on a plain positional record: a syntactically
        // valid JSON object missing (or null-ing) either field deserializes successfully with
        // that field C#-null. registry.Get(null) and JsonDocument.Parse(null) both throw
        // ArgumentNullException, which the catch above does not cover — guard explicitly rather
        // than let a semantically-incomplete-but-valid message escape HandleAsync unhandled.
        if (ev is not null && !string.IsNullOrEmpty(ev.TypeName))
        {
            var tenantColumn = registry.Get(ev.TypeName)?.TenantColumn;
            if (tenantColumn is not null && !string.IsNullOrEmpty(ev.PayloadJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(ev.PayloadJson);
                    tenantId = ExtractString(doc.RootElement, tenantColumn);
                }
                catch (JsonException)
                {
                    // Malformed payload JSON: same fallback as above.
                }
            }
        }

        await dlq.InsertAsync(
            new DlqMessage(
                SourceTopic: Header("dlq.source_topic") ?? "",
                ConsumerGroup: Header("dlq.consumer_group") ?? "",
                MessageKey: key,
                MessageValue: value,
                ExceptionType: Header("dlq.exception_type"),
                ExceptionMessage: Header("dlq.exception_message"),
                Attempts: int.TryParse(attemptsRaw, out var a) ? a : 0,
                FailedAt: DateTime.TryParse(
                    failedAtRaw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out var f)
                        ? f
                        : DateTime.UtcNow,
                TenantId: tenantId));

        logger.LogInformation(
            "[DlqMonitor] Recorded DLQ message key={Key} sourceTopic={SourceTopic}",
            key,
            Header("dlq.source_topic"));
    }

    private static string? ExtractString(JsonElement payload, string propertyName)
    {
        if (payload.TryGetProperty(propertyName, out var v))
            return v.ValueKind == JsonValueKind.String ? v.GetString()
                 : v.ValueKind == JsonValueKind.Null   ? null
                 : v.ToString();

        var camel = char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
        if (payload.TryGetProperty(camel, out var vc))
            return vc.ValueKind == JsonValueKind.String ? vc.GetString()
                 : vc.ValueKind == JsonValueKind.Null   ? null
                 : vc.ToString();

        return null;
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}
