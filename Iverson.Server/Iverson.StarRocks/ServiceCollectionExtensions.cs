using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iverson.StarRocks;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStarRocks(
        this IServiceCollection services,
        string connectionString,
        EngagementResilienceOptions? resilienceOptions = null,
        bool engagementEnabled = true,
        EngagementQueryLimitOptions? queryLimitOptions = null)
    {
        var resolvedQueryLimits = queryLimitOptions ?? EngagementQueryLimitOptions.Default;

        // CSR finding #6: MaxTopK (ObjectSearchGrpcService) and MaxRelationDepth
        // (ObjectMappingGrpcService) are enforced in Iverson.Api, outside the StarRocks SQL
        // builders that consume the rest of this options object — registering it here as its
        // own singleton lets those services take it as a normal constructor dependency instead
        // of a second, parallel configuration path.
        services.AddSingleton(resolvedQueryLimits);

        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<EngagementRepository>>();
            return new EngagementRepository(connectionString, logger, resilienceOptions, resolvedQueryLimits);
        });
        services.AddSingleton<IEngagementStoreQueryExecutor>(sp => sp.GetRequiredService<EngagementRepository>());
        services.AddSingleton<IEngagementStoreEntityStore>(sp => sp.GetRequiredService<EngagementRepository>());

        if (engagementEnabled)
            services.AddSingleton<IEngagementStoreSearchService>(sp => sp.GetRequiredService<EngagementRepository>());
        else
            services.AddSingleton<IEngagementStoreSearchService>(new DisabledEngagementStoreSearchService());

        services.AddSingleton(new EngagementHealthChecker(connectionString));

        if (engagementEnabled)
            services.AddSingleton<IEngagementStoreHealthCheck>(sp => sp.GetRequiredService<EngagementHealthChecker>());
        else
            services.AddSingleton<IEngagementStoreHealthCheck>(new DisabledEngagementStoreHealthCheck());

        return services;
    }
}
