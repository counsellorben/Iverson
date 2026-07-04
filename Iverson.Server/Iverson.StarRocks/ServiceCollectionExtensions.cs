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
        services.AddSingleton<IStarRocksRepository>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<StarRocksRepository>>();
            return new StarRocksRepository(connectionString, logger, resilienceOptions);
        });
        return services;
    }
}
