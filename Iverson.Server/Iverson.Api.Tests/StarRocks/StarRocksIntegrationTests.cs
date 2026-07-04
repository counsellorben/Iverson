using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Iverson.Api.Schema;
using Iverson.Api.StarRocks;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Iverson.Sql;
using Iverson.StarRocks;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.StarRocks;

public sealed class StarRocksContainerFixture : IAsyncLifetime
{
    private const int MysqlPort = 9030;
    private const int HttpPort  = 8030;

    private readonly IContainer _container = new ContainerBuilder()
        .WithImage("starrocks/allin1-ubuntu:latest")
        .WithPortBinding(MysqlPort, true)
        .WithPortBinding(HttpPort, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(MysqlPort))
        .Build();

    public string ConnectionString { get; private set; } = null!;
    public StarRocksRepository Repository { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        ConnectionString = new MySqlConnectionStringBuilder
        {
            Server                  = _container.Hostname,
            Port                    = (uint)_container.GetMappedPublicPort(MysqlPort),
            Database                = "iverson_test",
            UserID                  = "root",
            Password                = "",
            AllowPublicKeyRetrieval = true,
        }.ToString();

        // The MySQL port accepts TCP connections well before StarRocks's FE/BE bootstrap
        // finishes and the engine is actually ready to execute queries — a bare port-open
        // wait strategy is not sufficient. Retry a real query until it succeeds or we give up.
        await WaitUntilQueryReadyAsync(TimeSpan.FromMinutes(2));

        Repository = new StarRocksRepository(ConnectionString, NullLogger<StarRocksRepository>.Instance);
    }

    private async Task WaitUntilQueryReadyAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var dbName = new MySqlConnectionStringBuilder(ConnectionString).Database;
        var probeConnectionString = new MySqlConnectionStringBuilder(ConnectionString)
        {
            Database = ""
        }.ToString();

        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var conn = new MySqlConnection(probeConnectionString);
                await conn.OpenAsync();
                await using var cmd = new MySqlCommand("SELECT 1", conn);
                await cmd.ExecuteScalarAsync();

                // StarRocksRepository.ApplyTableAsync creates the target database lazily
                // (via its own private EnsureDatabaseAsync), but plain QueryAsync/ExecuteAsync
                // calls do not — they connect straight to `iverson_test`, which doesn't exist
                // yet on a fresh container. Create it here so every later test (whether or
                // not it goes through ApplyTableAsync first) can query against a real schema.
                await using var createCmd = new MySqlCommand($"CREATE DATABASE IF NOT EXISTS `{dbName}`", conn);
                await createCmd.ExecuteNonQueryAsync();
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }

        throw new TimeoutException(
            $"StarRocks did not become query-ready within {timeout}.", lastError);
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

public sealed class StarRocksIntegrationTests(StarRocksContainerFixture fixture)
    : IClassFixture<StarRocksContainerFixture>
{
    private readonly StarRocksRepository _repo = fixture.Repository;

    // Use unique table names per test to avoid state leakage — the container and its
    // schema persist for the whole test class (IClassFixture), and StarRocks has no
    // per-test transactional rollback to lean on.
    private static string UniqueTable() =>
        "tbl_" + Guid.NewGuid().ToString("N")[..8];

    private static SchemaDescriptor AuthorSchema(string tableName) => new()
    {
        TypeName      = "Author",
        TableName     = tableName,
        KeyColumn     = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns =
        [
            new ColumnDescriptor("Name",        "text",        false),
            new ColumnDescriptor("Bio",         "text",        true),
            new ColumnDescriptor("Rating",      "integer",     true),
            new ColumnDescriptor("PublishedAt", "timestamptz", true),
        ],
        FkColumns    = [],
        VectorFields = [],
        ChunkFields  = [],
        Relations    = []
    };

    private static async Task CreateAndSeedAuthorsAsync(
        StarRocksRepository repo, string tableName, params (string Id, string Name, string? Bio, int? Rating, string? PublishedAt)[] rows)
    {
        var schema = new StarRocksTableSchema(
            tableName,
            new StarRocksColumnSchema("Id", "VARCHAR(36)", false),
            [
                new StarRocksColumnSchema("Name",        "STRING",   false),
                new StarRocksColumnSchema("Bio",         "STRING",   true),
                new StarRocksColumnSchema("Rating",      "INT",      true),
                new StarRocksColumnSchema("PublishedAt", "DATETIME", true),
            ]);

        await repo.ApplyTableAsync(schema);

        foreach (var row in rows)
        {
            var bio         = row.Bio is null ? "NULL" : $"'{row.Bio}'";
            var rating      = row.Rating is null ? "NULL" : row.Rating.ToString();
            var publishedAt = row.PublishedAt is null ? "NULL" : $"'{row.PublishedAt}'";
            await repo.ExecuteAsync(
                $"INSERT INTO `{tableName}` VALUES ('{row.Id}', '{row.Name}', {bio}, {rating}, {publishedAt})");
        }
    }

    private static SchemaRegistry BuildRegistry(params SchemaDescriptor[] schemas)
    {
        var sql = Substitute.For<IPostgresRepository>();
        sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);
        var registry = new SchemaRegistry(sql, NullLogger<SchemaRegistry>.Instance);
        foreach (var schema in schemas)
            registry.RegisterAsync(schema).GetAwaiter().GetResult();
        return registry;
    }

    [Fact]
    public async Task Fixture_ContainerStartsAndAcceptsQueries()
    {
        var result = await _repo.QueryAsync<int>("SELECT 1");
        result.Should().ContainSingle().Which.Should().Be(1);
    }
}
