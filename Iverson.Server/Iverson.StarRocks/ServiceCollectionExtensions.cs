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
        services.AddSingleton<IEngagementStoreQueryExecutor>(sp => sp.GetRequiredService<StarRocksRepository>());
        services.AddSingleton<IEngagementStoreEntityStore>(sp => sp.GetRequiredService<StarRocksRepository>());
        services.AddSingleton<IEngagementStoreSearchService>(sp => sp.GetRequiredService<StarRocksRepository>());

        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<StarRocksSchemaManager>>();
            return new StarRocksSchemaManager(connectionString, logger, resilienceOptions);
        });
        services.AddSingleton<IEngagementStoreSchemaManager>(sp => sp.GetRequiredService<StarRocksSchemaManager>());

        services.AddSingleton(new StarRocksHealthChecker(connectionString));
        services.AddSingleton<IEngagementStoreHealthCheck>(sp => sp.GetRequiredService<StarRocksHealthChecker>());

        return services;
    }
}
