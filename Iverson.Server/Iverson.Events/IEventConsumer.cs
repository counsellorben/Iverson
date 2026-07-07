using Confluent.Kafka;

namespace Iverson.Events;

public interface IEventConsumer
{
    Task ConsumeAsync(
        string topic,
        string groupId,
        Func<string, string, CancellationToken, Task> handler,
        CancellationToken cancellationToken);

    /// <summary>
    /// A thinner consume-and-commit loop than <see cref="ConsumeAsync"/>: no retry/backoff,
    /// no DLQ routing on failure (there is no "DLQ of the DLQ"), but gives the handler direct
    /// access to the message's Kafka headers. Used only by consumers that need header
    /// metadata the standard <see cref="ConsumeAsync"/>/<c>MessageDispatcher</c> pipeline
    /// doesn't expose (e.g. <c>DlqMonitorConsumer</c> reading the <c>dlq.*</c> headers
    /// <c>MessageDispatcher.DeadLetterAsync</c> writes).
    /// </summary>
    Task ConsumeRawAsync(
        string topic,
        string groupId,
        Func<string, string, Headers, CancellationToken, Task> handler,
        CancellationToken cancellationToken);
}
