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
        services.AddSingleton<IRecordStoreQueryExecutor>(sp => sp.GetRequiredService<PostgresRepository>());
        services.AddSingleton<IRecordStoreTransactionRunner>(sp => sp.GetRequiredService<PostgresRepository>());

        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<PostgresSchemaManager>>();
            return new PostgresSchemaManager(connectionString, logger);
        });
        services.AddSingleton<IRecordStoreSchemaManager>(sp => sp.GetRequiredService<PostgresSchemaManager>());

        services.AddSingleton<IEntityRepository>(sp =>
            new EntityRepository(sp.GetRequiredService<IRecordStoreQueryExecutor>()));

        services.AddSingleton<ISchemaRegistryRepository>(sp =>
            new SchemaRegistryRepository(sp.GetRequiredService<IRecordStoreQueryExecutor>()));

        // CSR round 12 §5.3: the worker's RegisterSchema adoption check and its request locks.
        services.AddSingleton<IExistingTableInspector>(sp =>
            new ExistingTableInspector(sp.GetRequiredService<IRecordStoreQueryExecutor>()));
        services.AddSingleton<IPrivilegedRequestLocks>(_ => new PostgresPrivilegedRequestLocks(connectionString));

        return services;
    }
}
