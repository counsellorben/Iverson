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

    // Pins the /health readiness-probe contract from Program.cs: with engagement disabled, the
    // registered IEngagementStoreHealthCheck must be the non-throwing DisabledEngagementStore-
    // HealthCheck, not EngagementHealthChecker. A later refactor that reverted this branch (e.g.
    // "simplify" back to always registering EngagementHealthChecker) would still compile and
    // still pass every other suite in the repo, but would make the api/worker pods permanently
    // unready on the engagementEnabled: false profile (values-laptop.yaml) — this is the test
    // that would actually catch that regression.
    [Fact]
    public void AddStarRocks_WithEngagementDisabled_RegistersDisabledHealthCheck()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<EngagementRepository>>(NullLogger<EngagementRepository>.Instance);
        services.AddStarRocks(ConnString, engagementEnabled: false);

        using var provider = services.BuildServiceProvider();
        var healthCheck = provider.GetRequiredService<IEngagementStoreHealthCheck>();

        healthCheck.Should().BeOfType<DisabledEngagementStoreHealthCheck>();
    }
}
