using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iverson.StarRocks;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStarRocks(
        this IServiceCollection services,
        string connectionString,
        EngagementResilienceOptions? resilienceOptions = null)
    {
        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<EngagementRepository>>();
            return new EngagementRepository(connectionString, logger, resilienceOptions);
        });
        services.AddSingleton<IEngagementStoreQueryExecutor>(sp => sp.GetRequiredService<EngagementRepository>());
        services.AddSingleton<IEngagementStoreEntityStore>(sp => sp.GetRequiredService<EngagementRepository>());
        services.AddSingleton<IEngagementStoreSearchService>(sp => sp.GetRequiredService<EngagementRepository>());

        services.AddSingleton(new EngagementHealthChecker(connectionString));
        services.AddSingleton<IEngagementStoreHealthCheck>(sp => sp.GetRequiredService<EngagementHealthChecker>());

        return services;
    }
}
