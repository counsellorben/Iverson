namespace Iverson.Events;

/// <summary>
/// Notified when a fire-and-forget Kafka publish (<see cref="IEventProducer.PublishFireAndForget{T}"/>)
/// fails to deliver, so the failure can be recorded for later automatic retry. Iverson.Events has no
/// knowledge of how or where this is persisted — the concrete implementation (backed by Postgres) is
/// supplied by Iverson.Api, the composition root, to keep this project free of a Iverson.Sql dependency.
/// </summary>
public interface IFailedPublishSink
{
    Task RecordAsync(string typeName, string key, string reason);
}

/// <summary>Default no-op sink so any caller of <see cref="ServiceCollectionExtensions.AddKafka"/> resolves
/// cleanly even if it never registers a real <see cref="IFailedPublishSink"/>.</summary>
public sealed class NullFailedPublishSink : IFailedPublishSink
{
    public Task RecordAsync(string typeName, string key, string reason) => Task.CompletedTask;
}
