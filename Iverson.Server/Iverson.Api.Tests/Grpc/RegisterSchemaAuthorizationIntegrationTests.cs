using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Iverson.Api.Authorization;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.Sql;
using Iverson.StarRocks;
using Iverson.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using NSubstitute;
using Qdrant.Client;
using Testcontainers.PostgreSql;
using Xunit;
using SchemaAuthorizationRules = Iverson.Api.Schema.AuthorizationRules;
using SchemaRowPermission      = Iverson.Api.Schema.RowPermission;
using SchemaFieldPermission    = Iverson.Api.Schema.FieldPermission;

namespace Iverson.Api.Tests.Grpc;

/// <summary>
/// Combines the three per-store TestContainers fixtures that already exist independently
/// (<c>Iverson.Sql.Tests/PostgresIntegrationTests.cs</c>, <c>Iverson.StarRocks.Tests/StarRocksIntegrationTests.cs</c>,
/// <c>Iverson.Api.Tests/Grpc/ObjectSearchVectorIntegrationTests.cs</c>) into a single composite fixture, so
/// <see cref="ObjectMappingGrpcService.RegisterSchema"/> can be exercised end to end against real Postgres,
/// StarRocks and Qdrant containers. No such 3-store fixture exists elsewhere in the repo — this one is scoped
/// to this file per the repo's convention of duplicating fixture code per test file rather than sharing it.
/// </summary>
public sealed class AllStoresContainerFixture : IAsyncLifetime
{
    private const int StarRocksMysqlPort = 9030;
    private const int QdrantGrpcPort     = 6334;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    private readonly IContainer _starRocks = new ContainerBuilder()
        .WithImage("starrocks/allin1-ubuntu:latest")
        .WithPortBinding(StarRocksMysqlPort, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(StarRocksMysqlPort))
        .Build();

    private readonly IContainer _qdrant = new ContainerBuilder()
        .WithImage("qdrant/qdrant:v1.18.2")
        .WithPortBinding(QdrantGrpcPort, assignRandomHostPort: true)
        .WithPortBinding(6333, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(QdrantGrpcPort))
        .Build();

    public string ConnectionString { get; private set; } = null!;
    public PostgresRepository PostgresRepository { get; private set; } = null!;
    public PostgresSchemaManager PostgresSchemaManager { get; private set; } = null!;
    public StarRocksSchemaManager StarRocksSchemaManager { get; private set; } = null!;
    public QdrantCollectionManager QdrantCollectionManager { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Start all three containers concurrently — StarRocks is by far the slowest to become
        // query-ready (its FE/BE bootstrap can take a couple of minutes), so starting Postgres
        // and Qdrant while it boots avoids paying for that latency three times over.
        await Task.WhenAll(_postgres.StartAsync(), _starRocks.StartAsync(), _qdrant.StartAsync());

        ConnectionString = _postgres.GetConnectionString();
        PostgresRepository = new PostgresRepository(
            ConnectionString, NullLogger<PostgresRepository>.Instance);
        PostgresSchemaManager = new PostgresSchemaManager(
            ConnectionString, NullLogger<PostgresSchemaManager>.Instance);

        var qdrantClient = new QdrantClient(
            _qdrant.Hostname, _qdrant.GetMappedPublicPort(QdrantGrpcPort), https: false);
        QdrantCollectionManager = new QdrantCollectionManager(
            qdrantClient, NullLogger<QdrantCollectionManager>.Instance);

        var starRocksConnectionString = new MySqlConnectionStringBuilder
        {
            Server                  = _starRocks.Hostname,
            Port                    = (uint)_starRocks.GetMappedPublicPort(StarRocksMysqlPort),
            Database                = "iverson_test",
            UserID                  = "root",
            Password                = "",
            AllowPublicKeyRetrieval = true,
        }.ToString();

        // The MySQL wire port accepts TCP connections well before StarRocks's FE/BE bootstrap
        // finishes and the engine is actually ready to execute queries — a bare port-open wait
        // strategy is not sufficient. Retry a real query until it succeeds or we give up. Copied
        // faithfully from Iverson.StarRocks.Tests/StarRocksIntegrationTests.cs's
        // StarRocksContainerFixture.WaitUntilQueryReadyAsync, which found this the hard way.
        await WaitUntilStarRocksQueryReadyAsync(starRocksConnectionString, TimeSpan.FromMinutes(3));

        StarRocksSchemaManager = new StarRocksSchemaManager(
            starRocksConnectionString, NullLogger<StarRocksSchemaManager>.Instance);
    }

    private static async Task WaitUntilStarRocksQueryReadyAsync(string connectionString, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var dbName = new MySqlConnectionStringBuilder(connectionString).Database;
        var probeConnectionString = new MySqlConnectionStringBuilder(connectionString)
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

                // StarRocksSchemaManager.ApplyTableAsync creates the target database lazily,
                // but plain queries connect straight to `iverson_test`, which doesn't exist yet
                // on a fresh container. Create it here so RegisterSchema's later
                // ApplyTableAsync call (and anything else) can rely on the database existing.
                await using var createCmd = new MySqlCommand($"CREATE DATABASE IF NOT EXISTS `{dbName}`", conn);
                await createCmd.ExecuteNonQueryAsync();

                // SELECT 1 (and DDL like CREATE DATABASE) only exercise the FE — StarRocks's FE
                // metadata service comes up and starts accepting the MySQL wire protocol well
                // before the BE (backend) process has finished starting and registered itself
                // with the FE. Any query that actually touches table data (CREATE TABLE, INSERT,
                // SELECT against a real table) fails with "Backend node not found" until the BE
                // is alive, which can take noticeably longer than FE readiness. Gate readiness on
                // BE aliveness too, not just FE.
                if (!await IsStarRocksBackendAliveAsync(conn))
                {
                    lastError = new Exception("StarRocks backend was never reported alive (SHOW BACKENDS Alive=false)");
                    await Task.Delay(TimeSpan.FromSeconds(3));
                    continue;
                }

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

    private static async Task<bool> IsStarRocksBackendAliveAsync(MySqlConnection conn)
    {
        await using var cmd = new MySqlCommand("SHOW BACKENDS", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var aliveOrdinal = -1;
        while (await reader.ReadAsync())
        {
            if (aliveOrdinal < 0)
                aliveOrdinal = reader.GetOrdinal("Alive");

            if (string.Equals(reader.GetString(aliveOrdinal), "true", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public async Task DisposeAsync()
    {
        await Task.WhenAll(
            _postgres.DisposeAsync().AsTask(),
            _starRocks.DisposeAsync().AsTask(),
            _qdrant.DisposeAsync().AsTask());
    }
}

/// <summary>
/// Proves that the new <see cref="TypeDescriptor.Authorization"/>/<see cref="SchemaDescriptor.Authorization"/>
/// metadata (added in Task 1, not yet wired into any live RPC) does not break the existing 3-store
/// schema-provisioning pipeline that <see cref="ObjectMappingGrpcService.RegisterSchema"/> drives against real
/// Postgres/StarRocks/Qdrant, and that the metadata survives a real Postgres-backed JSON round trip.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RegisterSchemaAuthorizationIntegrationTests(AllStoresContainerFixture fixture)
    : IClassFixture<AllStoresContainerFixture>
{
    private static TypeDescriptor SimpleType(string name, params string[] extraScalars)
    {
        var td = new TypeDescriptor { TypeName = name };
        td.Properties.Add(new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true });
        foreach (var s in extraScalars)
            td.Properties.Add(new PropertyDescriptor { Name = s, ClrType = ClrType.ClrString });
        return td;
    }

    [Fact]
    public async Task RegisterSchema_WithAuthorizationRules_ProvisionsAllStoresAndRoundTripsThroughPostgres()
    {
        var registry = new SchemaRegistry(
            new SchemaRegistryRepository(fixture.PostgresRepository),
            NullLogger<SchemaRegistry>.Instance);

        // A real orchestrator (not a mock) is required here: this test's entire purpose is to
        // prove RegisterSchema provisions all 3 real backing stores end to end, so the DDL/schema
        // registration work it delegates to ISchemaRegistrationOrchestrator must actually run
        // against the fixture's real Postgres/StarRocks/Qdrant managers.
        var schemaRegistration = new SchemaRegistrationOrchestrator(
            fixture.PostgresSchemaManager,
            fixture.QdrantCollectionManager,
            fixture.StarRocksSchemaManager,
            Substitute.For<IEmbeddingService>(),
            registry);

        var sut = new ObjectMappingGrpcService(
            Substitute.For<IEntityRepository>(),
            Substitute.For<IRecordStoreTransactionRunner>(),
            Substitute.For<IOutboxPublisher>(),
            registry,
            Substitute.For<IRelationValidator>(),
            Substitute.For<IEntityKeyAccessor>(),
            Substitute.For<IOutboxWriter>(),
            NullLogger<ObjectMappingGrpcService>.Instance,
            Substitute.For<IActingUserAccessor>(),
            Substitute.For<IRowFieldAuthorizationEvaluator>(),
            Substitute.For<IEntityRelationResolver>(),
            schemaRegistration);

        var typeDesc = SimpleType("ArticleWithAuth", "Title", "OwnerId");
        typeDesc.Authorization = new Client.Contracts.AuthorizationRules
        {
            OwnerField = "OwnerId",
            RowPermissions = { new Client.Contracts.RowPermission { Role = "admin", CanReadAll = true } }
        };

        var response = await sut.RegisterSchema(
            new SchemaRequest { RootType = typeDesc },
            TestServerCallContext.Create());

        response.Success.Should().BeTrue();
        response.Registered.Should().Contain("ArticleWithAuth");

        var expectedAuthorization = new SchemaAuthorizationRules(
            "OwnerId",
            new List<SchemaRowPermission> { new("admin", true, false, false) },
            new List<SchemaFieldPermission>());

        // Build a second, fully independent SchemaRegistry backed by a fresh PostgresRepository
        // pointed at the same container connection string, and reload from it. This forces an
        // actual JSON deserialize round-trip through Postgres rather than reading back the first
        // registry's in-memory cache — the thing this test actually exists to prove.
        var secondRepository = new PostgresRepository(
            fixture.ConnectionString, NullLogger<PostgresRepository>.Instance);
        var secondRegistry = new SchemaRegistry(
            new SchemaRegistryRepository(secondRepository), NullLogger<SchemaRegistry>.Instance);
        await secondRegistry.LoadAsync();

        var loaded = secondRegistry.Get("ArticleWithAuth");
        loaded.Should().NotBeNull();

        // AuthorizationRules/RowPermission/FieldPermission carry IReadOnlyList<T> properties, and
        // record-generated Equals() compares those by reference — a fresh JSON deserialize always
        // produces a different List<T> instance, so .Should().Be(...) would fail even on a
        // correct round-trip. BeEquivalentTo does a structural comparison instead, matching this
        // test project's existing convention for the same situation (SchemaBuilderTests.cs).
        loaded!.Authorization.Should().BeEquivalentTo(expectedAuthorization);
    }
}
