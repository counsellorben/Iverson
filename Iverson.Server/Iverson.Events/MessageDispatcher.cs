using System.Diagnostics;
using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace Iverson.Events;

/// <summary>Immutable context for one message being dispatched.</summary>
public sealed record DispatchContext(
    string SourceTopic,
    string ConsumerGroup,
    string Key,
    string Value,
    Headers Headers);

/// <summary>Retry/backoff knobs. Defaults: 3 attempts, exponential 1s/2s/4s.</summary>
public sealed class MessageDispatcherOptions
{
    public int MaxAttempts { get; init; } = 3;
    public Func<int, TimeSpan> Backoff { get; init; } =
        attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
}

/// <summary>
/// Runs a projection handler under the delivery contract:
///   - ordinary exception  → retry (bounded, with backoff), then dead-letter;
///   - PoisonMessageException → dead-letter immediately (no retry);
///   - success or successful dead-letter → return normally (caller commits the offset);
///   - DLQ write itself fails → throw (caller must NOT commit — halt rather than lose).
/// </summary>
public sealed class MessageDispatcher(
    IProducer<string, string> producer,
    ILogger<MessageDispatcher> logger,
    MessageDispatcherOptions? options = null)
{
    private readonly MessageDispatcherOptions _options = options ?? new MessageDispatcherOptions();

    public async Task DispatchAsync(
        DispatchContext ctx,
        Func<string, string, CancellationToken, Task> handler,
        CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                await handler(ctx.Key, ctx.Value, ct);
                return;
            }
            catch (PoisonMessageException ex)
            {
                logger.LogCritical(
                    ex,
                    "[Dispatch] Poison message topic={Topic} key={Key} — routing to DLQ",
                    ctx.SourceTopic, ctx.Key);
                await DeadLetterAsync(ctx, ex, attempt + 1, ct);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                attempt++;
                if (attempt >= _options.MaxAttempts)
                {
                    logger.LogCritical(
                        ex,
                        "[Dispatch] Exhausted {Max} attempts topic={Topic} key={Key} — routing to DLQ",
                        _options.MaxAttempts, ctx.SourceTopic, ctx.Key);
                    await DeadLetterAsync(ctx, ex, attempt, ct);
                    return;
                }

                Telemetry.ConsumerRetries.Add(1);
                Activity.Current?.SetTag("messaging.retry_count", attempt);
                logger.LogWarning(
                    ex,
                    "[Dispatch] Transient failure attempt {Attempt}/{Max} topic={Topic} key={Key}",
                    attempt, _options.MaxAttempts, ctx.SourceTopic, ctx.Key);
                await Task.Delay(_options.Backoff(attempt), ct);
            }
        }
    }

    private async Task DeadLetterAsync(DispatchContext ctx, Exception ex, int attempts, CancellationToken ct)
    {
        var headers = new Headers
        {
            { "dlq.source_topic",      Encoding.UTF8.GetBytes(ctx.SourceTopic) },
            { "dlq.consumer_group",    Encoding.UTF8.GetBytes(ctx.ConsumerGroup) },
            { "dlq.exception_type",    Encoding.UTF8.GetBytes(ex.GetType().FullName ?? "Unknown") },
            { "dlq.exception_message", Encoding.UTF8.GetBytes(Truncate(ex.Message, 512)) },
            { "dlq.attempts",          Encoding.UTF8.GetBytes(attempts.ToString()) },
            { "dlq.failed_at",         Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToString("O")) },
        };

        var traceparent = ctx.Headers.FirstOrDefault(h => h.Key == "traceparent")?.GetValueBytes();
        if (traceparent is not null)
            headers.Add("traceparent", traceparent);

        try
        {
            await producer.ProduceAsync(
                EntityTopics.Dlq,
                new Message<string, string> { Key = ctx.Key, Value = ctx.Value, Headers = headers },
                ct);
        }
        catch (Exception produceEx)
        {
            logger.LogCritical(
                produceEx,
                "[Dispatch] FAILED to write to DLQ topic={Topic} key={Key} — offset will NOT be committed",
                ctx.SourceTopic, ctx.Key);
            throw;
        }

        Telemetry.ConsumerDlqRouted.Add(1);
        Activity.Current?
            .SetTag("messaging.dlq", true)
            .SetTag("messaging.dlq.reason", ex.GetType().Name);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
