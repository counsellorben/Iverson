using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit;

namespace Iverson.Sql.Tests;

/// <summary>
/// Exercises <see cref="DlqRepository"/> against a real Postgres instance (not a mocked
/// <see cref="IRecordStoreQueryExecutor"/>) specifically to catch the <c>timestamptz</c>/CLR type
/// mismatch class of bug that mocked unit tests structurally cannot detect: Npgsql maps
/// <c>timestamptz</c> to <see cref="DateTime"/> by default, and Dapper's fast record-materialization
/// path requires an exact type match, so a record typing that column as <see cref="DateTimeOffset"/>
/// crashes <see cref="DlqRepository.ListUnreplayedAsync"/> on every real call (though never in a
/// mocked test, since mocks never invoke Dapper's actual reader-to-record path).
/// </summary>
/// <remarks>
/// This test lives in <c>Iverson.Sql.Tests</c>, not <c>Iverson.Api.Tests</c>, because
/// <c>Iverson.Sql</c> is what actually owns <see cref="DlqRepository"/>/<see cref="DlqRow"/>/
/// <see cref="DlqMessage"/>. The real schema (<c>Iverson.Api.Reconciliation.DlqSchema</c>) is
/// <c>internal</c> to <c>Iverson.Api</c>, and <c>Iverson.Api</c> only has an
/// <c>InternalsVisibleTo</c> entry for <c>Iverson.Api.Tests</c>, not <c>Iverson.Sql.Tests</c> — so
/// rather than widening that internal type's visibility surface just for this test, the table
/// schema below is inlined to match <c>DlqSchema.Table</c>'s exact column list/types.
/// </remarks>
public sealed class DlqRepositoryPostgresContainerFixture : IAsyncLifetime
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

public sealed class DlqRepositoryPostgresIntegrationTests(DlqRepositoryPostgresContainerFixture fixture)
    : IClassFixture<DlqRepositoryPostgresContainerFixture>
{
    // Mirrors Iverson.Api.Reconciliation.DlqSchema.TableName exactly — kept as a literal here since
    // DlqSchema is internal to Iverson.Api (see class remarks above).
    private const string TableName = "IversonDlqMessages";

    // Mirrors Iverson.Api.Reconciliation.DlqSchema.Table's exact column list/types.
    private static readonly TableSchema Schema = new(
        TableName,
        new ColumnSchema("Id", "uuid", false),
        new List<ColumnSchema>
        {
            new("SourceTopic",      "text", false),
            new("ConsumerGroup",    "text", false),
            new("MessageKey",       "text", false),
            new("MessageValue",     "text", false),
            new("ExceptionType",    "text", true),
            new("ExceptionMessage", "text", true),
            new("Attempts",         "integer", false),
            new("FailedAt",         "timestamptz", false),
            new("Replayed",         "boolean", false),
        });

    private readonly PostgresRepository _repo = fixture.Repository;
    private readonly PostgresSchemaManager _schemaManager = fixture.SchemaManager;

    [Fact]
    public async Task InsertAsync_ThenListUnreplayedAsync_RoundTripsAgainstRealPostgres()
    {
        // TableName is a fixed constant — DROP/recreate for a known-clean state regardless of test
        // re-runs against the same container.
        await _repo.ExecuteAsync($"""DROP TABLE IF EXISTS "{TableName}" """);
        await _schemaManager.ApplySchemaAsync(Schema);

        var repo = new DlqRepository(TableName, _repo);
        var message = new DlqMessage(
            "iverson.events.article", "iverson.consumer.intelligence-store", "article-123",
            """{"id":"article-123"}""", "System.Exception", "boom", 3, DateTime.UtcNow);

        await repo.InsertAsync(message);

        // This is the exact call that 500'd live in Task 10 — it must not throw, and must
        // actually materialize the row via Dapper's real reader, not a mock.
        var rows = (await repo.ListUnreplayedAsync(10)).ToList();

        rows.Should().ContainSingle();
        rows[0].SourceTopic.Should().Be("iverson.events.article");
        rows[0].MessageKey.Should().Be("article-123");
        rows[0].Attempts.Should().Be(3);
        rows[0].Replayed.Should().BeFalse();
    }

    [Fact]
    public async Task MarkReplayedAsync_ExcludesRowFromListUnreplayedAsync()
    {
        await _repo.ExecuteAsync($"""DROP TABLE IF EXISTS "{TableName}" """);
        await _schemaManager.ApplySchemaAsync(Schema);

        var repo = new DlqRepository(TableName, _repo);
        var message = new DlqMessage(
            "topic", "group", "key-to-replay", "value", null, null, 1, DateTime.UtcNow);
        await repo.InsertAsync(message);

        var inserted = (await repo.ListUnreplayedAsync(10)).ToList();
        inserted.Should().ContainSingle();

        await repo.MarkReplayedAsync(inserted[0].Id);

        var afterReplay = await repo.ListUnreplayedAsync(10);
        afterReplay.Should().BeEmpty();
    }
}
