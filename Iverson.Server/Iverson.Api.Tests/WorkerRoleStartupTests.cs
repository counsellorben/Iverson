using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using Iverson.Api.Tests.Helpers;
using Iverson.Events;
using Iverson.Sql;
using Iverson.StarRocks;
using Iverson.Vector;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests;

/// <summary>
/// CSR round 12 §6.3, through the real Program.cs in the worker role. The worker owns all startup
/// DDL: <see cref="WorkerStoreBootstrap"/> runs before the registry loads, and the worker answers
/// /health only once that startup has finished. The api role's counterpart is
/// <see cref="ApiStartupReadinessPipelineTests"/>.
/// </summary>
public class WorkerRoleStartupTests
{
    private sealed class WorkerTestFactory : AuthTestWebApplicationFactory
    {
        public readonly ConcurrentQueue<string> Calls = new();
        public readonly IRecordStoreQueryExecutor Db = Substitute.For<IRecordStoreQueryExecutor>();
        public readonly IEngagementStoreHealthCheck StarRocks = Substitute.For<IEngagementStoreHealthCheck>();
        public readonly IVectorSchemaManager Vector = Substitute.For<IVectorSchemaManager>();
        public readonly IEventBrokerHealthCheck Kafka = Substitute.For<IEventBrokerHealthCheck>();
        public readonly IRecordStoreSchemaManager SchemaManager = Substitute.For<IRecordStoreSchemaManager>();
        public readonly ISchemaRegistryRepository RegistryRepository = Substitute.For<ISchemaRegistryRepository>();

        public WorkerTestFactory()
        {
            Db.QuerySingleOrDefaultAsync<int>(Arg.Any<string>()).Returns(1);
            Db.QuerySingleOrDefaultAsync<bool>(Arg.Any<string>()).Returns(true);
            StarRocks.CheckHealthAsync().Returns(EngagementHealthStatus.Healthy);
            Vector.PingAsync().Returns(true);
            Kafka.PingAsync().Returns(true);

            RegistryRepository.EnsureTableAsync().Returns(Task.CompletedTask).AndDoes(_ => Calls.Enqueue("registry-table"));
            RegistryRepository.LoadAllAsync()
                .Returns(Enumerable.Empty<(string TypeName, string SchemaJson)>())
                .AndDoes(_ => Calls.Enqueue("registry-load"));
            SchemaManager.EnsureRolesAsync().Returns(Task.CompletedTask).AndDoes(_ => Calls.Enqueue("roles"));
            SchemaManager.EnsureApiLoginAsync(default, default!, default!, default)
                .ReturnsForAnyArgs(Task.CompletedTask)
                .AndDoes(ci => Calls.Enqueue($"api-login:{ci.Arg<string?>()}"));
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.UseSetting("WorkloadRole", "worker");
            builder.UseSetting("Postgres:ApiLoginPassword", "worker-test-password");
            // The worker's own engagement consumer needs the provisioner login and a Kafka broker.
            builder.UseSetting("Engagement:Enabled", "false");

            builder.ConfigureServices(services =>
            {
                // The startup block under test runs before any hosted service starts. Dropping them
                // keeps the worker's Kafka consumers, which would otherwise dial a broker that is not
                // there and hold the host open for 30 s on disposal, out of a unit-weight test.
                services.RemoveAll<IHostedService>();

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
            });
        }
    }

    [Fact]
    public async Task WorkerStartup_RunsTheStoreBootstrapBeforeTheRegistryLoads_AndThenServesHealth()
    {
        using var factory = new WorkerTestFactory();

        var client = factory.CreateClient(); // boots the host, which runs Program.cs's startup block
        var response = await client.GetAsync("/health");

        var calls = factory.Calls.ToList();
        calls.Should().ContainInOrder("registry-table", "roles", "api-login:worker-test-password", "registry-load");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        // Only /health's probe: the api-role startup block, which also probes, does not run here.
        await factory.SchemaManager.Received(1).EnsureCanEnterRuntimeRoleAsync();
        // The grants and the tables they lock on are the worker's production wiring, not a stub detail.
        await factory.SchemaManager.Received(1).EnsureApiLoginAsync(
            "worker-test-password", ApiLoginGrants.Statements, ApiLoginGrants.Tables, Arg.Any<TimeSpan>());
    }
}
