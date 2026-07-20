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
        //
        // Connect with no default database selected (mirrors the pre-Task-5
        // StarRocksSchemaManager.EnsureDatabaseAsync, which used the same trick) so the very
        // first statement executed through StarRocksRepository's readiness-gated RunAsync can
        // itself be a CREATE DATABASE — proving the gate absorbs the FE-ready-but-BE-not-ready
        // race for a real, data-touching operation without requiring the target database to
        // already exist. (EnsureTenantProvisionedAsync is not used here: its GRANT statements
        // require a pre-existing `iverson_app` user that this bare test container never creates.)
        var adminConnectionString = new MySqlConnectionStringBuilder(_connectionString) { Database = "" }.ToString();
        var repo = new StarRocksRepository(
            adminConnectionString,
            NullLogger<StarRocksRepository>.Instance,
            new StarRocksResilienceOptions { BackendReadyTimeout = TimeSpan.FromMinutes(3) });

        var qualifiedTable = "`iverson_readiness_test`.`readiness_probe`";
        var createTableDdl = $"""
            CREATE TABLE IF NOT EXISTS {qualifiedTable} (
                `Id` VARCHAR(36) NOT NULL,
                `Name` STRING NOT NULL
            ) ENGINE=OLAP
            PRIMARY KEY(`Id`)
            DISTRIBUTED BY HASH(`Id`) BUCKETS 4
            PROPERTIES ("replication_num" = "1")
            """;

        var act = async () =>
        {
            await repo.ExecuteAsync("CREATE DATABASE IF NOT EXISTS `iverson_readiness_test`");
            await repo.ExecuteAsync(createTableDdl);
        };

        await act.Should().NotThrowAsync(
            "the repository's own readiness gate should absorb the FE/BE startup race internally");

        var healthChecker = new StarRocksHealthChecker(_connectionString);
        var healthy = await healthChecker.IsHealthyAsync();

        healthy.Should().BeTrue(
            "once the backend is alive, the extended SHOW BACKENDS health check should report true");
    }

    [Fact]
    public async Task CheckHealthAsync_UnknownUser_ReturnsAuthPending()
    {
        // Confirms AccessDenied (1045) is really what a live StarRocks server returns for a
        // nonexistent user at the connection/login step — this is standard MySQL wire-protocol
        // behavior, but StarRocks's own privilege model could in principle diverge, so this is
        // verified against the real image rather than assumed from MySqlConnector's enum alone.
        // Doesn't need BE to have registered (login failure happens before any query runs), so
        // this can assert immediately once the port is open — no BackendReadyTimeout needed.
        var badConnectionString = new MySqlConnector.MySqlConnectionStringBuilder
        {
            Server                  = _container.Hostname,
            Port                    = (uint)_container.GetMappedPublicPort(MysqlPort),
            Database                = "iverson_readiness_test",
            UserID                  = "nonexistent_user_12345",
            Password                = "wrong",
            AllowPublicKeyRetrieval = true,
        }.ToString();

        var healthChecker = new StarRocksHealthChecker(badConnectionString);

        var status = await healthChecker.CheckHealthAsync();

        status.Should().Be(StarRocksHealthStatus.AuthPending);
    }
}
