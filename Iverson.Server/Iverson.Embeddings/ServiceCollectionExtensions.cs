using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Iverson.Embeddings;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddEmbeddings(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<EmbeddingServiceOptions>(config.GetSection(EmbeddingServiceOptions.Section));

        services.AddHttpClient(Telemetry.HttpClientName, (sp, client) =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<EmbeddingServiceOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl);
        });

        services.AddSingleton<IEmbeddingService, OpenAiEmbeddingService>();
        return services;
    }
}
