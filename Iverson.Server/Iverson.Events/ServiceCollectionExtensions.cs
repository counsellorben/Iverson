using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iverson.Events;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddKafka(
        this IServiceCollection services,
        string bootstrapServers,
        int numPartitions = 12)
    {
        services.AddSingleton(_ =>
            new ProducerBuilder<string, string>(new ProducerConfig
            {
                BootstrapServers = bootstrapServers,
                LingerMs         = 5,
                BatchSize        = 65536,
                CompressionType  = CompressionType.Lz4
            })
            .Build());

        services.AddSingleton<IEventProducer, KafkaProducer>();

        services.AddSingleton<IEventConsumer>(sp =>
        {
            var dispatcher = new MessageDispatcher(
                sp.GetRequiredService<IProducer<string, string>>(),
                sp.GetRequiredService<ILogger<MessageDispatcher>>());
            return new KafkaConsumer(
                bootstrapServers,
                sp.GetRequiredService<ILogger<KafkaConsumer>>(),
                dispatcher,
                numPartitions);
        });

        return services;
    }
}
