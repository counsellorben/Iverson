using System.Net;
using System.Text.Json;
using FluentAssertions;
using Iverson.Api.Tests.Helpers;
using Iverson.Events;
using Iverson.Sql;
using Iverson.StarRocks;
using Iverson.Vector;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Iverson.Api.Tests;

/// <summary>
/// CSR round 12 §6.3, through the real Program.cs in the api role (the test host's default). The
/// api runs no DDL and no seed, and probes only <c>iverson_runtime</c>. It starts even when its
/// registry cannot load yet. /health reports not-ready until its login holds the worker's grants,
/// can enter <c>iverson_runtime</c>, and the registry has loaded.
/// </summary>
public class ApiStartupReadinessPipelineTests
{
    private sealed class ApiStartupTestFactory : AuthTestWebApplicationFactory
    {
        public readonly IRecordStoreQueryExecutor Db = Substitute.For<IRecordStoreQueryExecutor>();
        public readonly IEngagementStoreHealthCheck StarRocks = Substitute.For<IEngagementStoreHealthCheck>();
        public readonly IVectorSchemaManager Vector = Substitute.For<IVectorSchemaManager>();
        public readonly IEventBrokerHealthCheck Kafka = Substitute.For<IEventBrokerHealthCheck>();
        public readonly IRecordStoreSchemaManager SchemaManager = Substitute.For<IRecordStoreSchemaManager>();
        public readonly ISchemaRegistryRepository RegistryRepository = Substitute.For<ISchemaRegistryRepository>();
        public readonly IEnrichmentStateRepository EnrichmentState = Substitute.For<IEnrichmentStateRepository>();
        public readonly IDocumentRerenderQueueRepository RerenderQueue = Substitute.For<IDocumentRerenderQueueRepository>();
        public readonly ITokenRevocationRepository TokenRevocations = Substitute.For<ITokenRevocationRepository>();
        public readonly ITenantRepository Tenants = Substitute.For<ITenantRepository>();

        public ApiStartupTestFactory(bool registryLoadFails = false)
        {
            if (registryLoadFails)
                RegistryRepository.LoadAllAsync()
                    .ThrowsAsync(new InvalidOperationException("relation \"_iverson_schema\" does not exist"));
            else
                RegistryRepository.LoadAllAsync().Returns(Enumerable.Empty<(string TypeName, string SchemaJson)>());

            Db.QuerySingleOrDefaultAsync<int>(Arg.Any<string>()).Returns(1);
            Db.QuerySingleOrDefaultAsync<bool>(Arg.Any<string>()).Returns(true);
            StarRocks.CheckHealthAsync().Returns(EngagementHealthStatus.Healthy);
            Vector.PingAsync().Returns(true);
            Kafka.PingAsync().Returns(true);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            // On, so the startup test can show the api ignores it: seeding is the worker's.
            builder.UseSetting("Tenancy:SeedLegacyTenants", "true");

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IRecordStoreQueryExecutor>();
                services.AddSingleton(Db);
                services.RemoveAll<IEngagementStoreHealthCheck>();
                services.AddSingleton(StarRocks);
                services.RemoveAll<IVectorSchemaManager>();
                services.AddSingleton(Vector);
                services.RemoveAll<IEventBrokerHealthCheck>();
                services.AddSingleton(Kafka);
                services.RemoveAll<IRecordStoreSchemaManager>();
                services.AddSingleton(SchemaManager);
                services.RemoveAll<ISchemaRegistryRepository>();
                services.AddSingleton(RegistryRepository);
                services.RemoveAll<IEnrichmentStateRepository>();
                services.AddSingleton(EnrichmentState);
                services.RemoveAll<IDocumentRerenderQueueRepository>();
                services.AddSingleton(RerenderQueue);
                services.RemoveAll<ITokenRevocationRepository>();
                services.AddSingleton(TokenRevocations);
                services.RemoveAll<ITenantRepository>();
                services.AddSingleton(Tenants);
            });
        }
    }

    private static async Task<JsonElement> ChecksAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("checks").Clone();
    }

    [Fact]
    public async Task GetHealth_WithTheGrantsHeldAndTheRegistryLoaded_IsReady()
    {
        using var factory = new ApiStartupTestFactory();

        var response = await factory.CreateClient().GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var checks = await ChecksAsync(response);
        checks.GetProperty("apiGrants").GetBoolean().Should().BeTrue();
        checks.GetProperty("runtimeRole").GetBoolean().Should().BeTrue();
        checks.GetProperty("registry").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GetHealth_WhenTheLoginLacksTheOutboxInsertGrant_IsNotReady()
    {
        using var factory = new ApiStartupTestFactory();
        factory.Db.QuerySingleOrDefaultAsync<bool>(Arg.Any<string>()).Returns(false);

        var response = await factory.CreateClient().GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var checks = await ChecksAsync(response);
        checks.GetProperty("apiGrants").GetBoolean().Should().BeFalse();
        checks.GetProperty("postgres").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GetHealth_WhenTheGrantProbeFails_IsNotReady()
    {
        // The table the probe names does not exist yet on a fresh install: has_table_privilege
        // raises 42P01 rather than answering false.
        using var factory = new ApiStartupTestFactory();
        factory.Db.QuerySingleOrDefaultAsync<bool>(Arg.Any<string>())
            .ThrowsAsync(new InvalidOperationException("relation \"IversonReconciliationQueue\" does not exist"));

        var response = await factory.CreateClient().GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await ChecksAsync(response)).GetProperty("apiGrants").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task GetHealth_WhenTheRuntimeRoleProbeFails_IsNotReady_EvenWithTheGrantsAndTheRegistry()
    {
        using var factory = new ApiStartupTestFactory();
        var client = factory.CreateClient(); // startup's own probe succeeds, so the registry loads

        // Only now does the probe fail: the login has lost iverson_runtime (or the CNPG membership
        // has not landed yet). The grants and the registry are fine, so only the probe can make
        // /health not-ready.
        factory.SchemaManager.EnsureCanEnterRuntimeRoleAsync()
            .ThrowsAsync(new InvalidOperationException("Role iverson_runtime exists but this connection is not a member of it"));

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var checks = await ChecksAsync(response);
        checks.GetProperty("runtimeRole").GetBoolean().Should().BeFalse();
        checks.GetProperty("apiGrants").GetBoolean().Should().BeTrue();
        checks.GetProperty("registry").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ApiHost_WhenTheRegistryCannotLoadAtStartup_StillStartsAndReportsNotReady()
    {
        using var factory = new ApiStartupTestFactory(registryLoadFails: true);
        var client = factory.CreateClient();

        (await client.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await client.GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await ChecksAsync(response)).GetProperty("registry").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task ApiStartup_RunsNoDdlAndNoSeed_AndProbesOnlyTheRuntimeRole()
    {
        using var factory = new ApiStartupTestFactory();

        _ = factory.CreateClient(); // boots the host, which runs Program.cs's startup block

        await factory.SchemaManager.Received(1).EnsureCanEnterRuntimeRoleAsync();
        await factory.SchemaManager.DidNotReceive().EnsureRolesAsync();
        await factory.SchemaManager.DidNotReceiveWithAnyArgs().ApplySchemaAsync(default!, default);
        await factory.SchemaManager.DidNotReceiveWithAnyArgs().EnsureApiLoginAsync(default, default!, default!, default);
        await factory.RegistryRepository.DidNotReceive().EnsureTableAsync();
        await factory.RegistryRepository.Received(1).LoadAllAsync();
        await factory.EnrichmentState.DidNotReceive().EnsureTableAsync();
        await factory.RerenderQueue.DidNotReceive().EnsureTableAsync();
        await factory.TokenRevocations.DidNotReceive().EnsureTableAsync();
        await factory.Tenants.DidNotReceiveWithAnyArgs().SeedIfMissingAsync(default!, default!, default!);
    }
}
