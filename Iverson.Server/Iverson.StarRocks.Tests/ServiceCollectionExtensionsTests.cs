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
        services.AddSingleton<ILogger<EngagementRepository>>(NullLogger<EngagementRepository>.Instance);
        services.AddStarRocks(ConnString);

        using var provider = services.BuildServiceProvider();
        var queryExecutor = provider.GetRequiredService<IEngagementStoreQueryExecutor>();
        var healthCheck = provider.GetRequiredService<IEngagementStoreHealthCheck>();
        var entityStore = provider.GetRequiredService<IEngagementStoreEntityStore>();

        queryExecutor.Should().BeOfType<EngagementRepository>();
        healthCheck.Should().BeOfType<EngagementHealthChecker>();
        entityStore.Should().BeOfType<EngagementRepository>();
    }

    [Fact]
    public void AddStarRocks_WithCustomResilienceOptions_RegistersResolvableRepository()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<EngagementRepository>>(NullLogger<EngagementRepository>.Instance);
        services.AddStarRocks(
            ConnString,
            new EngagementResilienceOptions { BackendReadyTimeout = TimeSpan.FromSeconds(5) });

        using var provider = services.BuildServiceProvider();
        var queryExecutor = provider.GetRequiredService<IEngagementStoreQueryExecutor>();
        var healthCheck = provider.GetRequiredService<IEngagementStoreHealthCheck>();
        var entityStore = provider.GetRequiredService<IEngagementStoreEntityStore>();

        queryExecutor.Should().BeOfType<EngagementRepository>();
        healthCheck.Should().BeOfType<EngagementHealthChecker>();
        entityStore.Should().BeOfType<EngagementRepository>();
    }
}
