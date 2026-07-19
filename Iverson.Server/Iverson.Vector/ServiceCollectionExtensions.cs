using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Qdrant.Client;

namespace Iverson.Vector;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddQdrant(
        this IServiceCollection services,
        string host,
        int port = 6334,
        string? apiKey = null)
    {
        services.AddSingleton(_ => new QdrantClient(host, port, https: false, apiKey: null));
        services.AddSingleton<QdrantVectorService>();
        services.AddSingleton<IVectorQueryService>(sp => sp.GetRequiredService<QdrantVectorService>());
        services.AddSingleton<IVectorWriteService>(sp => sp.GetRequiredService<QdrantVectorService>());

        services.AddSingleton(sp => new QdrantCollectionManager(
            sp.GetRequiredService<QdrantClient>(), apiKey!, sp.GetRequiredService<ILogger<QdrantCollectionManager>>()));
        services.AddSingleton<IVectorSchemaManager>(sp => sp.GetRequiredService<QdrantCollectionManager>());

        services.AddSingleton(new QdrantTenantScope(apiKey!));

        return services;
    }
}
