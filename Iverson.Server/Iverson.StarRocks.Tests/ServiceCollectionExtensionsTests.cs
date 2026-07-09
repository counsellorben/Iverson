using FluentAssertions;
using Iverson.StarRocks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Iverson.StarRocks.Tests;

public class ServiceCollectionExtensionsTests
{
    private const string ConnString = "Server=localhost;Port=9030;Database=iverson;Uid=root;Pwd=;";

    [Fact]
    public void AddStarRocks_RegistersResolvableRepository()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<StarRocksRepository>>(NullLogger<StarRocksRepository>.Instance);
        services.AddSingleton<ILogger<StarRocksSchemaManager>>(NullLogger<StarRocksSchemaManager>.Instance);
        services.AddStarRocks(ConnString);

        using var provider = services.BuildServiceProvider();
        var queryExecutor = provider.GetRequiredService<IStarRocksQueryExecutor>();
        var schemaManager = provider.GetRequiredService<IStarRocksSchemaManager>();
        var healthCheck = provider.GetRequiredService<IStarRocksHealthCheck>();
        var entityStore = provider.GetRequiredService<IStarRocksEntityStore>();

        queryExecutor.Should().BeOfType<StarRocksRepository>();
        schemaManager.Should().BeOfType<StarRocksSchemaManager>();
        healthCheck.Should().BeOfType<StarRocksHealthChecker>();
        entityStore.Should().BeOfType<StarRocksRepository>();
    }

    [Fact]
    public void AddStarRocks_WithCustomResilienceOptions_RegistersResolvableRepository()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<StarRocksRepository>>(NullLogger<StarRocksRepository>.Instance);
        services.AddSingleton<ILogger<StarRocksSchemaManager>>(NullLogger<StarRocksSchemaManager>.Instance);
        services.AddStarRocks(
            ConnString,
            new StarRocksResilienceOptions { BackendReadyTimeout = TimeSpan.FromSeconds(5) });

        using var provider = services.BuildServiceProvider();
        var queryExecutor = provider.GetRequiredService<IStarRocksQueryExecutor>();
        var schemaManager = provider.GetRequiredService<IStarRocksSchemaManager>();
        var healthCheck = provider.GetRequiredService<IStarRocksHealthCheck>();
        var entityStore = provider.GetRequiredService<IStarRocksEntityStore>();

        queryExecutor.Should().BeOfType<StarRocksRepository>();
        schemaManager.Should().BeOfType<StarRocksSchemaManager>();
        healthCheck.Should().BeOfType<StarRocksHealthChecker>();
        entityStore.Should().BeOfType<StarRocksRepository>();
    }
}
