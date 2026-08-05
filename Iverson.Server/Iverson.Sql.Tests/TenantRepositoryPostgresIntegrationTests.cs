using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit;

namespace Iverson.Sql.Tests;

/// <summary>
/// Exercises <see cref="TenantRepository"/> against a real Postgres instance for exactly the
/// reason <see cref="DlqRepositoryPostgresIntegrationTests"/> exists: Npgsql maps
/// <c>timestamptz</c> to <see cref="DateTime"/>, Dapper's record-materialization path matches
/// constructor parameters by type, and so a row record typing that column as
/// <see cref="DateTimeOffset"/> throws on every real call while every mocked test passes.
/// <para>
/// <see cref="TenantRow"/> carried that exact defect after it had already been found and fixed for
/// <see cref="DlqRow"/> — <c>ListTenants</c> and <c>GetTenant</c> failed at runtime with
/// "a parameterless default constructor or one matching signature ... is required", which took the
/// load test's tenant provisioning down with them. <see cref="TenantRepositoryTests"/> could not
/// see it: it substitutes <see cref="IRecordStoreQueryExecutor"/> and constructs
/// <see cref="TenantRow"/> by hand, so Dapper never runs.
/// </para>
/// </summary>
public sealed class TenantRepositoryPostgresContainerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    public PostgresRepository Repository { get; private set; } = null!;
    public PostgresSchemaManager SchemaManager { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Repository = new PostgresRepository(
            _container.GetConnectionString(),
            NullLogger<PostgresRepository>.Instance);
        SchemaManager = new PostgresSchemaManager(
            _container.GetConnectionString(),
            NullLogger<PostgresSchemaManager>.Instance);
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

public sealed class TenantRepositoryPostgresIntegrationTests(TenantRepositoryPostgresContainerFixture fixture)
    : IClassFixture<TenantRepositoryPostgresContainerFixture>
{
    // Mirrors the live table created by tenant provisioning; "CreatedAt" is timestamptz, which is
    // the column whose CLR mapping this test exists to pin.
    private const string TableName = "IversonTenants";

    private static readonly TableSchema Schema = new(
        TableName,
        new ColumnSchema("Id", "text", false),
        new List<ColumnSchema>
        {
            new("DisplayName", "text",        false),
            new("Status",      "text",        false),
            new("CreatedAt",   "timestamptz", false),
        });

    private readonly PostgresRepository _repo = fixture.Repository;
    private readonly PostgresSchemaManager _schemaManager = fixture.SchemaManager;

    private async Task<TenantRepository> FreshRepositoryAsync()
    {
        await _repo.ExecuteAsync($"""DROP TABLE IF EXISTS "{TableName}" """);
        await _schemaManager.ApplySchemaAsync(Schema);
        return new TenantRepository(TableName, _repo);
    }

    [Fact]
    public async Task ListAsync_MaterializesThroughRealDapper()
    {
        var repo = await FreshRepositoryAsync();
        await repo.InsertAsync("tenant-alpha", "Alpha", "active");

        // The exact call that threw live. It must not throw, and must materialize via Dapper's
        // real reader rather than a mock.
        var rows = (await repo.ListAsync()).ToList();

        rows.Should().ContainSingle();
        rows[0].Id.Should().Be("tenant-alpha");
        rows[0].DisplayName.Should().Be("Alpha");
        rows[0].Status.Should().Be("active");
        rows[0].CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task GetAsync_MaterializesThroughRealDapper()
    {
        var repo = await FreshRepositoryAsync();
        await repo.InsertAsync("tenant-beta", "Beta", "active");

        var row = await repo.GetAsync("tenant-beta");

        row.Should().NotBeNull();
        row!.DisplayName.Should().Be("Beta");
    }

    [Fact]
    public async Task SeedIfMissingAsync_IsIdempotent()
    {
        var repo = await FreshRepositoryAsync();

        await repo.SeedIfMissingAsync("tenant-gamma", "Gamma", "active");
        await repo.SeedIfMissingAsync("tenant-gamma", "Gamma renamed", "suspended");

        var rows = (await repo.ListAsync()).ToList();
        rows.Should().ContainSingle();
        rows[0].DisplayName.Should().Be("Gamma");
    }
}
