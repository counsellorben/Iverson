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

        // Mirrors Program.cs startup ordering: the iverson_runtime role must exist before any
        // ApplySchemaAsync call that GRANTs to it for a tenant-scoped table.
        await SchemaManager.EnsureRuntimeRoleAsync();
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

    // ── Row-Level Security bootstrap (Part B1) ───────────────────────────────

    private async Task<bool> PolicyExistsAsync(string table, string policyName) =>
        await _repo.QuerySingleOrDefaultAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM pg_policies WHERE tablename = @Table AND policyname = @Policy)",
            new { Table = table, Policy = policyName });

    private async Task<bool> RlsEnabledAsync(string table) =>
        await _repo.QuerySingleOrDefaultAsync<bool>(
            "SELECT rowsecurity FROM pg_tables WHERE tablename = @Table",
            new { Table = table });

    private async Task<int> RuntimeGrantCountAsync(string table) =>
        await _repo.QuerySingleOrDefaultAsync<int>(
            """
            SELECT COUNT(*) FROM information_schema.role_table_grants
            WHERE table_name = @Table AND grantee = 'iverson_runtime'
            """,
            new { Table = table });

    [Fact]
    public async Task EnsureRuntimeRoleAsync_IsIdempotent_WhenCalledTwice()
    {
        var act = async () => await _schemaManager.EnsureRuntimeRoleAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ApplySchemaAsync_TenantScopedTable_GetsPolicyRlsAndGrant()
    {
        var table = UniqueTable();
        var schema = new TableSchema(
            table,
            new ColumnSchema("id", "uuid", IsNullable: false),
            [
                new ColumnSchema("name", "text", IsNullable: false),
                new ColumnSchema("tenant_id", "text", IsNullable: false),
            ],
            TenantColumn: "tenant_id");

        await _schemaManager.ApplySchemaAsync(schema);

        (await PolicyExistsAsync(table, $"{table}_tenant_isolation")).Should().BeTrue();
        (await RlsEnabledAsync(table)).Should().BeTrue();
        (await RuntimeGrantCountAsync(table)).Should().Be(4); // SELECT, INSERT, UPDATE, DELETE
    }

    [Fact]
    public async Task ApplySchemaAsync_NonTenantScopedTable_GetsNoPolicyRlsOrGrant()
    {
        var table = UniqueTable();
        var schema = new TableSchema(
            table,
            new ColumnSchema("id", "uuid", IsNullable: false),
            [new ColumnSchema("name", "text", IsNullable: false)]);

        await _schemaManager.ApplySchemaAsync(schema);

        (await PolicyExistsAsync(table, $"{table}_tenant_isolation")).Should().BeFalse();
        (await RlsEnabledAsync(table)).Should().BeFalse();
        (await RuntimeGrantCountAsync(table)).Should().Be(0);
    }

    [Fact]
    public async Task ApplySchemaAsync_ReRegisteringTenantScopedTable_DoesNotThrow()
    {
        var table = UniqueTable();
        var schema = new TableSchema(
            table,
            new ColumnSchema("id", "uuid", IsNullable: false),
            [
                new ColumnSchema("name", "text", IsNullable: false),
                new ColumnSchema("tenant_id", "text", IsNullable: false),
            ],
            TenantColumn: "tenant_id");

        await _schemaManager.ApplySchemaAsync(schema);

        var act = async () => await _schemaManager.ApplySchemaAsync(schema);
        await act.Should().NotThrowAsync();

        (await PolicyExistsAsync(table, $"{table}_tenant_isolation")).Should().BeTrue();
        (await RlsEnabledAsync(table)).Should().BeTrue();
        (await RuntimeGrantCountAsync(table)).Should().Be(4);
    }

    [Fact]
    public async Task ApplySchemaAsync_SelfHealsPreB1Table_WhenTenantColumnAddedAfterPhysicalTableExisted()
    {
        var table = UniqueTable();

        // Simulate a table whose descriptor already carried a TenantColumn under Part A but
        // whose physical DDL predates this change — construct it via a raw CREATE TABLE that
        // bypasses ApplySchemaAsync's tenant-DDL branch entirely, so no policy/RLS/grant exist.
        await _repo.ExecuteAsync($"""
            CREATE TABLE "{table}" (
                "id" uuid PRIMARY KEY,
                "name" text NOT NULL,
                "tenant_id" text NOT NULL
            )
            """);

        (await PolicyExistsAsync(table, $"{table}_tenant_isolation")).Should().BeFalse();
        (await RlsEnabledAsync(table)).Should().BeFalse();
        (await RuntimeGrantCountAsync(table)).Should().Be(0);

        var schema = new TableSchema(
            table,
            new ColumnSchema("id", "uuid", IsNullable: false),
            [
                new ColumnSchema("name", "text", IsNullable: false),
                new ColumnSchema("tenant_id", "text", IsNullable: false),
            ],
            TenantColumn: "tenant_id");

        await _schemaManager.ApplySchemaAsync(schema);

        (await PolicyExistsAsync(table, $"{table}_tenant_isolation")).Should().BeTrue();
        (await RlsEnabledAsync(table)).Should().BeTrue();
        (await RuntimeGrantCountAsync(table)).Should().Be(4);
    }

    // ── Schema-drift detection (Task 2 of array-column-mapping) ─────────────

    // All 18 SQL types PostgresSchemaManager can be asked to apply — the 9 scalar entries in
    // SchemaBuilder.ScalarTypeMap and the 9 array entries in ArrayTypeOverrides. Hardcoded here
    // rather than reflected off ClrType because Iverson.Sql.Tests has no reference to Iverson.Api
    // (where ClrType/SchemaBuilder live) and adding one purely for this list is out of scope.
    private static readonly string[] AllMappedSqlTypes =
    [
        "TEXT", "INTEGER", "BIGINT", "REAL", "DOUBLE PRECISION", "BOOLEAN", "TIMESTAMPTZ", "UUID", "BYTEA",
        "TEXT[]", "INTEGER[]", "BIGINT[]", "REAL[]", "DOUBLE PRECISION[]", "BOOLEAN[]", "TIMESTAMPTZ[]", "UUID[]", "BYTEA[]"
    ];

    private sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }

    [Fact]
    public async Task ApplySchemaAsync_MatchingColumnType_NoDrift()
    {
        var table = UniqueTable();
        var schema = new TableSchema(
            table,
            new ColumnSchema("id",   "uuid", IsNullable: false),
            [new ColumnSchema("tag", "TEXT", IsNullable: true)]);

        await _schemaManager.ApplySchemaAsync(schema);

        var act = async () => await _schemaManager.ApplySchemaAsync(schema, SchemaDriftPolicy.Throw);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ApplySchemaAsync_DifferingColumnType_ThrowsUnderThrowPolicy()
    {
        var table = UniqueTable();
        var v1 = new TableSchema(
            table,
            new ColumnSchema("id",     "uuid", IsNullable: false),
            [new ColumnSchema("count", "INTEGER", IsNullable: true)]);
        await _schemaManager.ApplySchemaAsync(v1);

        var v2 = new TableSchema(
            table,
            new ColumnSchema("id",     "uuid", IsNullable: false),
            [new ColumnSchema("count", "BIGINT", IsNullable: true)]);

        var act = async () => await _schemaManager.ApplySchemaAsync(v2, SchemaDriftPolicy.Throw);

        var thrown = await act.Should().ThrowAsync<SchemaDriftException>();
        thrown.Which.Message.Should().Contain(table);
        thrown.Which.Message.Should().Contain("count");
        thrown.Which.Message.Should().Contain("integer");
        thrown.Which.Message.Should().Contain("BIGINT");
    }

    [Fact]
    public async Task ApplySchemaAsync_DifferingColumnType_LogsUnderWarnPolicy()
    {
        var table = UniqueTable();
        var v1 = new TableSchema(
            table,
            new ColumnSchema("id",     "uuid", IsNullable: false),
            [new ColumnSchema("count", "INTEGER", IsNullable: true)]);
        await _schemaManager.ApplySchemaAsync(v1);

        var v2 = new TableSchema(
            table,
            new ColumnSchema("id",     "uuid", IsNullable: false),
            [new ColumnSchema("count", "BIGINT", IsNullable: true)]);

        var capturingLogger = new CapturingLogger<PostgresSchemaManager>();
        var warnManager = new PostgresSchemaManager(fixture.ConnectionString, capturingLogger);

        var act = async () => await warnManager.ApplySchemaAsync(v2, SchemaDriftPolicy.Warn);
        await act.Should().NotThrowAsync();

        capturingLogger.Warnings.Should().ContainSingle(w =>
            w.Contains(table) && w.Contains("count") && w.Contains("integer") && w.Contains("BIGINT"));
    }

    [Fact]
    public async Task ApplySchemaAsync_Timestamptz_ScalarAndArray_AreNotDrift()
    {
        var table = UniqueTable();
        var schema = new TableSchema(
            table,
            new ColumnSchema("id",         "uuid", IsNullable: false),
            [
                new ColumnSchema("seen_at",  "TIMESTAMPTZ",   IsNullable: true),
                new ColumnSchema("seen_ats", "TIMESTAMPTZ[]", IsNullable: true),
            ]);

        await _schemaManager.ApplySchemaAsync(schema);

        var act = async () => await _schemaManager.ApplySchemaAsync(schema, SchemaDriftPolicy.Throw);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ApplySchemaAsync_AllMappedSqlTypes_RoundTripWithoutDrift()
    {
        var table = UniqueTable();
        var columns = AllMappedSqlTypes
            .Select((sqlType, i) => new ColumnSchema($"col_{i}", sqlType, IsNullable: true))
            .ToList();

        var schema = new TableSchema(
            table,
            new ColumnSchema("id", "uuid", IsNullable: false),
            columns);

        await _schemaManager.ApplySchemaAsync(schema);

        var act = async () => await _schemaManager.ApplySchemaAsync(schema, SchemaDriftPolicy.Throw);
        await act.Should().NotThrowAsync();
    }

    // ── End-to-end array round-trip (Task 7 of array-column-mapping) ────────

    [Fact]
    public async Task ArrayProperties_RoundTripThroughJsonPopulateRecordAndRowToJson_AsJsonArrays()
    {
        var table = UniqueTable();
        await _schemaManager.ApplySchemaAsync(new TableSchema(
            table,
            new ColumnSchema("id",     "uuid",       IsNullable: false),
            [
                new ColumnSchema("tags",   "TEXT[]",    IsNullable: true),
                new ColumnSchema("scores", "INTEGER[]", IsNullable: true),
            ]));

        var id = Guid.NewGuid();
        var json = $$$"""{"id":"{{{id}}}","tags":["a","b"],"scores":[1,2,3]}""";

        var upsertSql = $"""
            INSERT INTO "{table}"
            SELECT * FROM json_populate_record(null::"{table}", @Json::json)
            """;
        await _repo.ExecuteAsync(upsertSql, new { Json = json });

        var readBack = await _repo.QuerySingleOrDefaultAsync<string>(
            $"""SELECT row_to_json(t)::text FROM "{table}" t WHERE id = @Id""",
            new { Id = id });

        readBack.Should().NotBeNull();

        // Negative control: with a scalar TEXT column holding a JSON-encoded array, row_to_json
        // would emit the stored text AS A JSON STRING — e.g. "tags":"[\"a\",\"b\"]" — because
        // Postgres has no idea the text happens to look like JSON. With a real TEXT[]/INTEGER[]
        // column it emits genuine JSON arrays: "tags":["a","b"]. Asserting the unescaped,
        // unquoted array form is what distinguishes the two; a regression to TEXT would produce
        // the escaped-string form and fail this assertion.
        readBack.Should().Contain("\"tags\":[\"a\",\"b\"]");
        readBack.Should().Contain("\"scores\":[1,2,3]");
        readBack.Should().NotContain("\\\"a\\\"");
    }

    [Fact]
    public async Task ApplySchemaAsync_AddsNonNullableArrayColumn_ToExistingTable()
    {
        var table = UniqueTable();
        var v1 = new TableSchema(
            table,
            new ColumnSchema("id",   "uuid", IsNullable: false),
            [new ColumnSchema("name", "text", IsNullable: false)]);
        await _schemaManager.ApplySchemaAsync(v1);

        // Adding a non-nullable array column to an already-existing table is the only DDL path
        // that invokes GetDefaultForType — a fresh CREATE TABLE emits no default at all, so only
        // this ALTER TABLE ADD COLUMN path would catch a malformed array default literal.
        var v2 = new TableSchema(
            table,
            new ColumnSchema("id",     "uuid", IsNullable: false),
            [
                new ColumnSchema("name", "text",    IsNullable: false),
                new ColumnSchema("tags",  "TEXT[]", IsNullable: false),
            ]);

        var act = async () => await _schemaManager.ApplySchemaAsync(v2);
        await act.Should().NotThrowAsync();

        var cols = (await _repo.QueryAsync<string>(
            $"SELECT column_name FROM information_schema.columns WHERE table_name = '{table}'"))
            .ToList();
        cols.Should().Contain("tags");
    }

    [Fact]
    public async Task ApplySchemaAsync_OrphanDrop_StillAppliesCleanly_AfterPriorColumnDrop()
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

        // "bio" is now a tombstoned pg_attribute row (attisdropped = true). A second apply with
        // the same v2 schema must not resurrect it as a phantom orphan-drop or false drift.
        var act = async () => await _schemaManager.ApplySchemaAsync(v2, SchemaDriftPolicy.Throw);
        await act.Should().NotThrowAsync();

        var cols = (await _repo.QueryAsync<string>(
            $"SELECT column_name FROM information_schema.columns WHERE table_name = '{table}'"))
            .ToList();
        cols.Should().NotContain("bio");
    }

    [Fact]
    public async Task ApplySchemaAsync_AddsArrayColumn_ToExistingTable_UsesDefaultForType()
    {
        var table = UniqueTable();

        var v1 = new TableSchema(
            table,
            new ColumnSchema("id",    "uuid", IsNullable: false),
            [new ColumnSchema("name", "text", IsNullable: false)]);
        await _schemaManager.ApplySchemaAsync(v1);

        var v2 = new TableSchema(
            table,
            new ColumnSchema("id",     "uuid", IsNullable: false),
            [
                new ColumnSchema("name", "text",     IsNullable: false),
                new ColumnSchema("tags", "TEXT[]",   IsNullable: false),
            ]);

        var act = async () => await _schemaManager.ApplySchemaAsync(v2);
        await act.Should().NotThrowAsync();

        var cols = (await _repo.QueryAsync<string>(
            $"SELECT column_name FROM information_schema.columns WHERE table_name = '{table}'"))
            .ToList();
        cols.Should().Contain("tags");
    }
}
