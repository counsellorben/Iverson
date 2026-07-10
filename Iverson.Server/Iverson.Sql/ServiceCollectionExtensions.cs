using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iverson.Sql;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPostgres(this IServiceCollection services, string connectionString)
    {
        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<PostgresRepository>>();
            return new PostgresRepository(connectionString, logger);
        });
        services.AddSingleton<IPostgresQueryExecutor>(sp => sp.GetRequiredService<PostgresRepository>());
        services.AddSingleton<IPostgresTransactionRunner>(sp => sp.GetRequiredService<PostgresRepository>());

        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<PostgresSchemaManager>>();
            return new PostgresSchemaManager(connectionString, logger);
        });
        services.AddSingleton<IPostgresSchemaManager>(sp => sp.GetRequiredService<PostgresSchemaManager>());

        return services;
    }
}
