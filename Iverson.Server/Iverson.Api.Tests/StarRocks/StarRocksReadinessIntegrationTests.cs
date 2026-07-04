using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Iverson.StarRocks;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Xunit;

namespace Iverson.Api.Tests.StarRocks;

// Deliberately does NOT reuse StarRocksContainerFixture from StarRocksIntegrationTests.cs —
// that fixture's own WaitUntilQueryReadyAsync already waits for the backend before handing
// out a Repository, which would defeat the point of this test: proving the *production*
// StarRocksReadinessGate (inside StarRocksRepository itself) absorbs the FE-ready-but-
// BE-not-ready race with no external help.
public sealed class StarRocksReadinessIntegrationTests : IAsyncLifetime
{
    private const int MysqlPort = 9030;

    private readonly IContainer _container = new ContainerBuilder()
        .WithImage("starrocks/allin1-ubuntu:latest")
        .WithPortBinding(MysqlPort, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(MysqlPort))
        .Build();

    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        _connectionString = new MySqlConnectionStringBuilder
        {
            Server                  = _container.Hostname,
            Port                    = (uint)_container.GetMappedPublicPort(MysqlPort),
            Database                = "iverson_readiness_test",
            UserID                  = "root",
            Password                = "",
            AllowPublicKeyRetrieval = true,
        }.ToString();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    [Fact]
    public async Task ProductionGate_AbsorbsFeBeRace_ForApplyTableAndHealthCheck()
    {
        // The container's MySQL port is open (WithWaitStrategy above) but StarRocks's own
        // FE/BE bootstrap may still be in progress — this is the exact race
        // StarRocksContainerFixture's WaitUntilQueryReadyAsync exists to hide from every
        // other integration test. Here we skip that external gate on purpose.
        var repo = new StarRocksRepository(
            _connectionString,
            NullLogger<StarRocksRepository>.Instance,
            new StarRocksResilienceOptions { BackendReadyTimeout = TimeSpan.FromMinutes(3) });

        var schema = new StarRocksTableSchema(
            "readiness_probe",
            new StarRocksColumnSchema("Id", "VARCHAR(36)", false),
            [new StarRocksColumnSchema("Name", "STRING", false)]);

        var act = async () => await repo.ApplyTableAsync(schema);

        await act.Should().NotThrowAsync(
            "the repository's own readiness gate should absorb the FE/BE startup race internally");

        var healthy = await repo.IsHealthyAsync();

        healthy.Should().BeTrue(
            "once the backend is alive, the extended SHOW BACKENDS health check should report true");
    }
}
