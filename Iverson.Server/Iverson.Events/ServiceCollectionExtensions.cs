using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iverson.Events;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddKafka(
        this IServiceCollection services,
        IConfiguration config,
        int numPartitions = 12)
    {
        services.Configure<KafkaOptions>(config.GetSection(KafkaOptions.Section));

        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<KafkaOptions>>().Value;
            var producerConfig = new ProducerConfig
            {
                BootstrapServers = options.BootstrapServers,
                LingerMs         = 5,
                BatchSize        = 65536,
                CompressionType  = CompressionType.Lz4
            };
            KafkaClientConfigFactory.ApplySecurity(producerConfig, options);
            return new ProducerBuilder<string, string>(producerConfig).Build();
        });

        services.AddSingleton<IFailedPublishSink, NullFailedPublishSink>();

        services.AddSingleton<IEventProducer, KafkaProducer>();

        services.AddSingleton<IEventConsumer>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<KafkaOptions>>().Value;
            var dispatcher = new MessageDispatcher(
                sp.GetRequiredService<IProducer<string, string>>(),
                sp.GetRequiredService<ILogger<MessageDispatcher>>());
            return new KafkaConsumer(
                options,
                sp.GetRequiredService<ILogger<KafkaConsumer>>(),
                dispatcher,
                numPartitions);
        });

        return services;
    }
}
