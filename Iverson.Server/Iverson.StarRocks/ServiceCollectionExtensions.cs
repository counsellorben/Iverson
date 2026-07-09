using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iverson.StarRocks;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStarRocks(
        this IServiceCollection services,
        string connectionString,
        StarRocksResilienceOptions? resilienceOptions = null)
    {
        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<StarRocksRepository>>();
            return new StarRocksRepository(connectionString, logger, resilienceOptions);
        });
        services.AddSingleton<IStarRocksQueryExecutor>(sp => sp.GetRequiredService<StarRocksRepository>());
        services.AddSingleton<IStarRocksSchemaManager>(sp => sp.GetRequiredService<StarRocksRepository>());
        services.AddSingleton<IStarRocksHealthCheck>(sp => sp.GetRequiredService<StarRocksRepository>());
        services.AddSingleton<IStarRocksEntityStore>(sp => sp.GetRequiredService<StarRocksRepository>());
        return services;
    }
}
