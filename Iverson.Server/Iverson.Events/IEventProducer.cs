namespace Iverson.Events;

public interface IEventProducer
{
    Task ProduceAsync<T>(string topic, string key, T message) where T : class;
    Task ProduceAsync(string topic, string key, string message);
    void PublishFireAndForget<T>(string topic, string key, T message) where T : class;
}
