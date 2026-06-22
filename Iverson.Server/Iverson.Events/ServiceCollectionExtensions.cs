using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;

namespace Iverson.Events;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddKafka(this IServiceCollection services, string bootstrapServers)
    {
        services.AddSingleton<IProducer<string, string>>(_ =>
            new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = bootstrapServers }).Build());

        services.AddSingleton<IEventProducer, KafkaProducer>();

        services.AddSingleton<IEventConsumer>(sp =>
        {
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<KafkaConsumer>>();
            return new KafkaConsumer(bootstrapServers, logger);
        });

        return services;
    }
}
