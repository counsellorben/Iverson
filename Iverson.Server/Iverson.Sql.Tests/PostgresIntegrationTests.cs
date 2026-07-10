using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Iverson.Sql.Tests;

public sealed class PostgresContainerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    public PostgresRepository Repository { get; private set; } = null!;
    public PostgresSchemaManager SchemaManager { get; private set; } = null!;
    public string ConnectionString => _container.GetConnectionString();

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

public sealed class PostgresIntegrationTests(PostgresContainerFixture fixture)
    : IClassFixture<PostgresContainerFixture>
{
    private readonly PostgresRepository _repo = fixture.Repository;
    private readonly PostgresSchemaManager _schemaManager = fixture.SchemaManager;

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

        await _schemaManager.ApplySchemaAsync(schema);

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

        await _schemaManager.ApplySchemaAsync(schema);

        var act = async () => await _schemaManager.ApplySchemaAsync(schema);
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
        await _schemaManager.ApplySchemaAsync(v1);

        var v2 = new TableSchema(
            table,
            new ColumnSchema("id",       "uuid", IsNullable: false),
            [
                new ColumnSchema("name", "text", IsNullable: false),
                new ColumnSchema("bio",  "text", IsNullable: true),
            ]);
        await _schemaManager.ApplySchemaAsync(v2);

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
        await _schemaManager.ApplySchemaAsync(v1);

        var v2 = new TableSchema(
            table,
            new ColumnSchema("id",    "uuid", IsNullable: false),
            [new ColumnSchema("name", "text", IsNullable: false)]);
        await _schemaManager.ApplySchemaAsync(v2);

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
        await _schemaManager.ApplySchemaAsync(new TableSchema(
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
        await _schemaManager.ApplySchemaAsync(new TableSchema(
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
        await _schemaManager.ApplySchemaAsync(new TableSchema(
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
        await _schemaManager.ApplySchemaAsync(new TableSchema(
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
        await _schemaManager.ApplySchemaAsync(new TableSchema(
            table,
            new ColumnSchema("id",        "uuid", IsNullable: false),
            [new ColumnSchema("authorId", "uuid", IsNullable: false)]));

        var indexes = (await _repo.QueryAsync<string>(
            $"SELECT indexname FROM pg_indexes WHERE tablename = '{table}'"))
            .ToList();

        indexes.Should().Contain(i => i.Contains("authorid", StringComparison.OrdinalIgnoreCase));
    }

    // ── ExecuteInTransactionAsync ─────────────────────────────────────────────

    [Fact]
    public async Task ExecuteInTransactionAsync_BothStatementsSucceed_BothCommit()
    {
        var tableA = UniqueTable();
        var tableB = UniqueTable();
        await _repo.ExecuteAsync($"CREATE TABLE IF NOT EXISTS \"{tableA}\" (id int PRIMARY KEY)");
        await _repo.ExecuteAsync($"CREATE TABLE IF NOT EXISTS \"{tableB}\" (id int PRIMARY KEY)");

        await _repo.ExecuteInTransactionAsync(async tx =>
        {
            await tx.ExecuteAsync($"INSERT INTO \"{tableA}\" (id) VALUES (1)");
            await tx.ExecuteAsync($"INSERT INTO \"{tableB}\" (id) VALUES (1)");
        });

        var a = await _repo.QuerySingleOrDefaultAsync<int>($"SELECT COUNT(*) FROM \"{tableA}\"");
        var b = await _repo.QuerySingleOrDefaultAsync<int>($"SELECT COUNT(*) FROM \"{tableB}\"");
        a.Should().Be(1);
        b.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteInTransactionAsync_SecondStatementThrows_FirstIsRolledBack()
    {
        var tableC = UniqueTable();
        await _repo.ExecuteAsync($"CREATE TABLE IF NOT EXISTS \"{tableC}\" (id int PRIMARY KEY)");

        var act = async () => await _repo.ExecuteInTransactionAsync(async tx =>
        {
            await tx.ExecuteAsync($"INSERT INTO \"{tableC}\" (id) VALUES (1)");
            await tx.ExecuteAsync("this is not valid sql");
        });

        await act.Should().ThrowAsync<Exception>();

        var count = await _repo.QuerySingleOrDefaultAsync<int>($"SELECT COUNT(*) FROM \"{tableC}\"");
        count.Should().Be(0); // rolled back, not committed
    }

    [Fact]
    public async Task ExecuteInTransactionAsync_RollbackFails_OriginalExceptionStillPropagates()
    {
        // If the connection is already broken when the catch block calls RollbackAsync(),
        // the rollback itself throws — that failure must never replace the original
        // exception, which is the one that actually explains what went wrong. Force this
        // by terminating our own backend from a second connection, then throwing from the
        // `work` delegate: by the time the catch block attempts the rollback, the
        // connection is already dead, so RollbackAsync() fails too.
        var act = async () => await _repo.ExecuteInTransactionAsync(async tx =>
        {
            var pid = await tx.QuerySingleOrDefaultAsync<int?>("SELECT pg_backend_pid()");

            await using var killer = new NpgsqlConnection(fixture.ConnectionString);
            await killer.OpenAsync();
            await using (var cmd = new NpgsqlCommand($"SELECT pg_terminate_backend({pid})", killer))
                await cmd.ExecuteNonQueryAsync();

            throw new InvalidOperationException("original failure");
        });

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Be("original failure");
    }
}
