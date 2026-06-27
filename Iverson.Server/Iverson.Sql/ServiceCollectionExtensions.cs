using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iverson.Sql;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPostgres(this IServiceCollection services, string connectionString)
    {
        services.AddSingleton<IPostgresRepository>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<PostgresRepository>>();
            return new PostgresRepository(connectionString, logger);
        });
        return services;
    }
}
