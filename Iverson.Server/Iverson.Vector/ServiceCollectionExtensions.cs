using Microsoft.Extensions.DependencyInjection;
using Qdrant.Client;

namespace Iverson.Vector;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddQdrant(
        this IServiceCollection services,
        string host,
        int port = 6334)
    {
        services.AddSingleton(_ => new QdrantClient(host, port));
        services.AddSingleton<IVectorService, QdrantVectorService>();
        return services;
    }
}
