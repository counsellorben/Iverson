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
        int numPartitions = 12,
        Func<Exception, bool>? isTransient = null)
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

        services.AddSingleton<IEventProducer, KafkaProducer>();

        services.AddSingleton<IAdminClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<KafkaOptions>>().Value;
            var adminConfig = new AdminClientConfig { BootstrapServers = options.BootstrapServers };
            KafkaClientConfigFactory.ApplySecurity(adminConfig, options);
            return new AdminClientBuilder(adminConfig).Build();
        });

        services.AddSingleton<IEventBrokerHealthCheck, KafkaBrokerHealthCheck>();

        services.AddSingleton(new MessageDispatcherOptions { IsTransient = isTransient ?? (_ => false) });

        services.AddSingleton(sp => new MessageDispatcher(
            sp.GetRequiredService<IProducer<string, string>>(),
            sp.GetRequiredService<ILogger<MessageDispatcher>>(),
            sp.GetRequiredService<MessageDispatcherOptions>()));

        services.AddSingleton<IEventConsumer>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<KafkaOptions>>().Value;
            return new KafkaConsumer(
                options,
                sp.GetRequiredService<ILogger<KafkaConsumer>>(),
                sp.GetRequiredService<MessageDispatcher>(),
                cfg => new ConsumerBuilder<string, string>(cfg).Build(),
                cfg => new AdminClientBuilder(cfg).Build(),
                numPartitions);
        });

        return services;
    }
}
