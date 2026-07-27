using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iverson.Embeddings;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddEmbeddings(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<EmbeddingServiceOptions>(config.GetSection(EmbeddingServiceOptions.Section));

        services.AddHttpClient(
            Telemetry.HttpClientName,
            (sp, client) =>
            {
                var opts = sp.GetRequiredService<
                    Microsoft.Extensions.Options.IOptions<EmbeddingServiceOptions>>().Value;
                client.BaseAddress = new Uri(opts.BaseUrl);
            });

        services.AddSingleton<IEmbeddingService, EmbeddingService>();
        return services;
    }

    public static IServiceCollection AddEnrichment(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<EnrichmentServiceOptions>(config.GetSection(EnrichmentServiceOptions.Section));

        services.AddHttpClient(
            Telemetry.EnrichmentHttpClientName,
            (sp, client) =>
            {
                var opts = sp.GetRequiredService<
                    Microsoft.Extensions.Options.IOptions<EnrichmentServiceOptions>>().Value;
                client.BaseAddress = new Uri(opts.BaseUrl);
                client.Timeout     = opts.Timeout;
            });

        services.AddSingleton<IEnrichmentService, EnrichmentService>();
        return services;
    }
}
