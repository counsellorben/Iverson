namespace Iverson.Events;

public interface IEventConsumer
{
    Task ConsumeAsync(
        string topic,
        string groupId,
        Func<string, string, CancellationToken, Task> handler,
        CancellationToken cancellationToken);
}
