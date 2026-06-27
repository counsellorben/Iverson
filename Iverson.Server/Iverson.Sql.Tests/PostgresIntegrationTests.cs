using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit;

namespace Iverson.Sql.Tests;

public sealed class PostgresContainerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    public PostgresRepository Repository { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Repository = new PostgresRepository(
            _container.GetConnectionString(),
            NullLogger<PostgresRepository>.Instance);
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

public sealed class PostgresIntegrationTests(PostgresContainerFixture fixture)
    : IClassFixture<PostgresContainerFixture>
{
    private readonly PostgresRepository _repo = fixture.Repository;

    // Use unique table names per test to avoid state leakage
    private static string UniqueTable() =>
        "tbl_" + Guid.NewGuid().ToString("N")[..8];

    // ── ApplySchemaAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task ApplySchemaAsync_CreatesTable_WhenNotExists()
    {
        var table = UniqueTable();
        var schema = new TableSchema(
            table,
            new ColumnSchema("id",    "uuid", IsNullable: false),
            [new ColumnSchema("name", "text", IsNullable: false)]);

        await _repo.ApplySchemaAsync(schema);

        // Confirm the table exists by querying information_schema
        var count = await _repo.QuerySingleOrDefaultAsync<int>(
            $"SELECT COUNT(*) FROM information_schema.tables WHERE table_name = '{table}'");
        count.Should().Be(1);
    }

    [Fact]
    public async Task ApplySchemaAsync_IsIdempotent_WhenCalledTwice()
    {
        var table  = UniqueTable();
        var schema = new TableSchema(
            table,
            new ColumnSchema("id",    "uuid", IsNullable: false),
            [new ColumnSchema("name", "text", IsNullable: false)]);

        await _repo.ApplySchemaAsync(schema);

        var act = async () => await _repo.ApplySchemaAsync(schema);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ApplySchemaAsync_AddsColumn_WhenSchemaExpands()
    {
        var table = UniqueTable();

        var v1 = new TableSchema(
            table,
            new ColumnSchema("id",    "uuid", IsNullable: false),
            [new ColumnSchema("name", "text", IsNullable: false)]);
        await _repo.ApplySchemaAsync(v1);

        var v2 = new TableSchema(
            table,
            new ColumnSchema("id",       "uuid", IsNullable: false),
            [
                new ColumnSchema("name", "text", IsNullable: false),
                new ColumnSchema("bio",  "text", IsNullable: true),
            ]);
        await _repo.ApplySchemaAsync(v2);

        var cols = (await _repo.QueryAsync<string>(
            $"SELECT column_name FROM information_schema.columns WHERE table_name = '{table}'"))
            .ToList();

        cols.Should().Contain("bio");
    }

    [Fact]
    public async Task ApplySchemaAsync_DropsColumn_WhenSchemaContracts()
    {
        var table = UniqueTable();

        var v1 = new TableSchema(
            table,
            new ColumnSchema("id",       "uuid", IsNullable: false),
            [
                new ColumnSchema("name", "text", IsNullable: false),
                new ColumnSchema("bio",  "text", IsNullable: true),
            ]);
        await _repo.ApplySchemaAsync(v1);

        var v2 = new TableSchema(
            table,
            new ColumnSchema("id",    "uuid", IsNullable: false),
            [new ColumnSchema("name", "text", IsNullable: false)]);
        await _repo.ApplySchemaAsync(v2);

        var cols = (await _repo.QueryAsync<string>(
            $"SELECT column_name FROM information_schema.columns WHERE table_name = '{table}'"))
            .ToList();

        cols.Should().NotContain("bio");
    }

    // ── ExecuteAsync / QueryAsync ─────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_InsertAndQueryAsync_RoundTrip()
    {
        var table = UniqueTable();
        await _repo.ApplySchemaAsync(new TableSchema(
            table,
            new ColumnSchema("id",    "uuid", IsNullable: false),
            [new ColumnSchema("name", "text", IsNullable: false)]));

        var id = Guid.NewGuid();
        var rows = await _repo.ExecuteAsync(
            $"INSERT INTO \"{table}\" (id, name) VALUES (@Id, @Name)",
            new { Id = id, Name = "Allen Iverson" });

        rows.Should().Be(1);

        var results = (await _repo.QueryAsync<string>(
            $"SELECT name FROM \"{table}\" WHERE id = @Id", new { Id = id }))
            .ToList();

        results.Should().ContainSingle().Which.Should().Be("Allen Iverson");
    }

    [Fact]
    public async Task QuerySingleOrDefaultAsync_ReturnsNull_WhenRowNotFound()
    {
        var table = UniqueTable();
        await _repo.ApplySchemaAsync(new TableSchema(
            table,
            new ColumnSchema("id",    "uuid", IsNullable: false),
            [new ColumnSchema("name", "text", IsNullable: false)]));

        var result = await _repo.QuerySingleOrDefaultAsync<string>(
            $"SELECT name FROM \"{table}\" WHERE id = @Id", new { Id = Guid.NewGuid() });

        result.Should().BeNull();
    }

    // ── json_populate_record upsert (the pattern used by RecordStoreConsumer) ─

    [Fact]
    public async Task UpsertViaJsonPopulateRecord_RoundTrips()
    {
        var table = UniqueTable();
        await _repo.ApplySchemaAsync(new TableSchema(
            table,
            new ColumnSchema("id",    "uuid", IsNullable: false),
            [new ColumnSchema("name", "text", IsNullable: false)]));

        var id  = Guid.NewGuid();
        var json = $$$"""{"id":"{{{id}}}","name":"Allen Iverson"}""";

        var upsertSql = $"""
            INSERT INTO "{table}"
            SELECT * FROM json_populate_record(null::"{table}", @Json::json)
            ON CONFLICT (id) DO UPDATE
            SET name = EXCLUDED.name
            """;

        await _repo.ExecuteAsync(upsertSql, new { Json = json });

        var name = await _repo.QuerySingleOrDefaultAsync<string>(
            $"SELECT name FROM \"{table}\" WHERE id = @Id", new { Id = id });

        name.Should().Be("Allen Iverson");
    }

    [Fact]
    public async Task UpsertViaJsonPopulateRecord_UpdatesExistingRow()
    {
        var table = UniqueTable();
        await _repo.ApplySchemaAsync(new TableSchema(
            table,
            new ColumnSchema("id",    "uuid", IsNullable: false),
            [new ColumnSchema("name", "text", IsNullable: false)]));

        var id  = Guid.NewGuid();
        var insertSql = $"""
            INSERT INTO "{table}"
            SELECT * FROM json_populate_record(null::"{table}", @Json::json)
            ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name
            """;

        await _repo.ExecuteAsync(insertSql, new { Json = $$$"""{"id":"{{{id}}}","name":"The Answer"}""" });
        await _repo.ExecuteAsync(insertSql, new { Json = $$$"""{"id":"{{{id}}}","name":"Allen Iverson"}""" });

        var name = await _repo.QuerySingleOrDefaultAsync<string>(
            $"SELECT name FROM \"{table}\" WHERE id = @Id", new { Id = id });

        name.Should().Be("Allen Iverson");
    }

    [Fact]
    public async Task ApplySchemaAsync_CreatesIndex_OnFkColumn()
    {
        var table = UniqueTable();
        await _repo.ApplySchemaAsync(new TableSchema(
            table,
            new ColumnSchema("id",        "uuid", IsNullable: false),
            [new ColumnSchema("authorId", "uuid", IsNullable: false)]));

        var indexes = (await _repo.QueryAsync<string>(
            $"SELECT indexname FROM pg_indexes WHERE tablename = '{table}'"))
            .ToList();

        indexes.Should().Contain(i => i.Contains("authorid", StringComparison.OrdinalIgnoreCase));
    }
}
