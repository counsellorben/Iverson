using FluentAssertions;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit;

namespace Iverson.Api.Tests.Reconciliation;

/// <summary>
/// Exercises the document re-render queue against a real Postgres instance (not a mocked
/// <see cref="IRecordStoreQueryExecutor"/>) specifically because the DDL's collapse guarantee and
/// column-casing are the two things a mock cannot catch: a target-less <c>ON CONFLICT DO NOTHING</c>
/// only actually collapses duplicates if the partial unique indexes exist, and a snake_case column
/// only actually maps blank if Dapper is doing exact-name matching against real Postgres output.
/// </summary>
public sealed class DocumentRerenderQueuePostgresContainerFixture : IAsyncLifetime
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

public sealed class DocumentRerenderQueuePostgresIntegrationTests(DocumentRerenderQueuePostgresContainerFixture fixture)
    : IClassFixture<DocumentRerenderQueuePostgresContainerFixture>
{
    private readonly PostgresRepository _repo = fixture.Repository;
    private readonly PostgresSchemaManager _schemaManager = fixture.SchemaManager;

    // DROP/recreate at the start of each test so the fixture's real Postgres container is left
    // in a known-clean state regardless of test re-runs against the same container (matches
    // ReconciliationQueuePostgresIntegrationTests).
    private async Task<DocumentRerenderQueueRepository> FreshQueueAsync()
    {
        await _repo.ExecuteAsync($"""DROP TABLE IF EXISTS "{DocumentRerenderQueueRepository.TableName}" """);
        var queue = new DocumentRerenderQueueRepository(_repo);
        await queue.EnsureTableAsync();
        return queue;
    }

    [Fact]
    public async Task EnqueueEntityAsync_SecondInsertForSameTenantTypeKey_LeavesExactlyOneRow()
    {
        var queue = await FreshQueueAsync();

        await queue.EnqueueEntityAsync("tenant-a", "Article", "article-1");
        await queue.EnqueueEntityAsync("tenant-a", "Article", "article-1");

        var count = await _repo.QuerySingleOrDefaultAsync<int>(
            $"""SELECT COUNT(*) FROM "{DocumentRerenderQueueRepository.TableName}" """);
        count.Should().Be(1);
    }

    [Fact]
    public async Task EnqueueEntityAsync_DifferentTenantSameTypeAndKey_LeavesTwoRows()
    {
        var queue = await FreshQueueAsync();

        await queue.EnqueueEntityAsync("tenant-a", "Article", "article-1");
        await queue.EnqueueEntityAsync("tenant-b", "Article", "article-1");

        var count = await _repo.QuerySingleOrDefaultAsync<int>(
            $"""SELECT COUNT(*) FROM "{DocumentRerenderQueueRepository.TableName}" """);
        count.Should().Be(2);
    }

    [Fact]
    public async Task EnqueueTypeAsync_SecondEnqueueForSameType_LeavesExactlyOneRow()
    {
        var queue = await FreshQueueAsync();

        await queue.EnqueueTypeAsync("Article");
        await queue.EnqueueTypeAsync("Article");

        var count = await _repo.QuerySingleOrDefaultAsync<int>(
            $"""SELECT COUNT(*) FROM "{DocumentRerenderQueueRepository.TableName}" """);
        count.Should().Be(1);
    }

    [Fact]
    public async Task EnqueueEntityAsync_AndEnqueueTypeAsync_ForSameType_CoexistAsTwoRows()
    {
        // The two partial unique indexes' predicates are disjoint: an entity row (EntityKey
        // non-null) and a type row (EntityKey null) for the same TypeName must NOT collapse
        // into each other.
        var queue = await FreshQueueAsync();

        await queue.EnqueueEntityAsync("tenant-a", "Article", "article-1");
        await queue.EnqueueTypeAsync("Article");

        var count = await _repo.QuerySingleOrDefaultAsync<int>(
            $"""SELECT COUNT(*) FROM "{DocumentRerenderQueueRepository.TableName}" """);
        count.Should().Be(2);
    }

    [Fact]
    public async Task PollAsync_ReturnsRowWithNonNullTypeNameAndEntityKey()
    {
        // A row-count assertion alone passes even when every column maps to a default value —
        // exactly what a snake_case column (Dapper's exact-name mapping, not enabled to be
        // underscore-aware in this repo) would produce. Assert the identity columns are non-null.
        var queue = await FreshQueueAsync();
        await queue.EnqueueEntityAsync("tenant-a", "Article", "article-1");

        var rows = (await queue.PollAsync(maxAttempts: 5, batchSize: 10)).ToList();

        rows.Should().HaveCount(1);
        rows[0].TypeName.Should().Be("Article");
        rows[0].EntityKey.Should().Be("article-1");
        rows[0].TenantId.Should().Be("tenant-a");
    }

    [Fact]
    public async Task PollAsync_ExcludesRowsAtOrAboveMaxAttempts()
    {
        var queue = await FreshQueueAsync();
        await queue.EnqueueEntityAsync("tenant-a", "Article", "article-1");
        var rows = (await queue.PollAsync(maxAttempts: 5, batchSize: 10)).ToList();
        rows.Should().HaveCount(1);
        var id = rows[0].Id;

        await queue.RecordFailureAsync(id, attempts: 5, lastError: "boom");

        var polledAfter = await queue.PollAsync(maxAttempts: 5, batchSize: 10);
        polledAfter.Should().BeEmpty();
    }

    [Fact]
    public async Task CountExhaustedAsync_CountsRowsAtOrAboveMaxAttempts_AndCountPendingCountsAllRows()
    {
        var queue = await FreshQueueAsync();
        await queue.EnqueueEntityAsync("tenant-a", "Article", "article-1");
        var rows = (await queue.PollAsync(maxAttempts: 5, batchSize: 10)).ToList();
        await queue.RecordFailureAsync(rows[0].Id, attempts: 5, lastError: "boom");

        await queue.EnqueueEntityAsync("tenant-a", "Article", "article-2");

        (await queue.CountPendingAsync()).Should().Be(2);
        (await queue.CountExhaustedAsync(maxAttempts: 5)).Should().Be(1);
    }

    [Fact]
    public async Task AdvanceCursorAsync_UpdatesCursorColumn()
    {
        var queue = await FreshQueueAsync();
        await queue.EnqueueTypeAsync("Article");
        var rows = (await queue.PollAsync(maxAttempts: 5, batchSize: 10)).ToList();
        var id = rows[0].Id;

        await queue.AdvanceCursorAsync(id, "cursor-42");

        var cursor = await _repo.QuerySingleOrDefaultAsync<string>(
            $"""SELECT "Cursor" FROM "{DocumentRerenderQueueRepository.TableName}" WHERE "Id" = @Id""",
            new { Id = id });
        cursor.Should().Be("cursor-42");
    }

    [Fact]
    public async Task DeleteRowAsync_RemovesRow()
    {
        var queue = await FreshQueueAsync();
        await queue.EnqueueEntityAsync("tenant-a", "Article", "article-1");
        var rows = (await queue.PollAsync(maxAttempts: 5, batchSize: 10)).ToList();

        await queue.DeleteRowAsync(rows[0].Id);

        (await queue.CountPendingAsync()).Should().Be(0);
    }

    [Fact]
    public async Task FetchKeysAndTenantsPagedAsync_PagesAllEntitiesOrderedByKey_AcrossTenants()
    {
        // FetchKeysAndTenantsPagedAsync is unscoped (like FetchAllAsync) because a type-level
        // re-render row means "every entity of this type, across every tenant" — scoping it to
        // one tenant would silently backfill only that tenant.
        await _repo.ExecuteAsync($"""DROP TABLE IF EXISTS "authors" """);
        // ApplySchemaAsync only creates columns present in ScalarColumns — AuthorSchema's
        // TenantColumn = "TenantId" names an existing column, it doesn't add one, so it must be
        // listed explicitly here for this test's real "authors" table to have it.
        var schema = SchemaFixtures.AuthorSchema() with
        {
            ScalarColumns =
            [
                .. SchemaFixtures.AuthorSchema().ScalarColumns,
                new ColumnDescriptor("TenantId", "text", true)
            ]
        };
        // TenantColumn triggers RLS policy/grant DDL that GRANTs to iverson_runtime — the role
        // must exist first (matches Program.cs's EnsureRuntimeRoleAsync-before-ApplySchemaAsync
        // ordering).
        await _schemaManager.EnsureRuntimeRoleAsync();
        await _schemaManager.ApplySchemaAsync(SchemaBuilder.ToTableSchema(schema));

        var entities = new EntityRepository(_repo);
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        await _repo.ExecuteAsync(
            """INSERT INTO "authors" ("Id", "Name", "TenantId") VALUES (@Id, @Name, @TenantId)""",
            new { Id = idA, Name = "Alice", TenantId = "tenant-a" });
        await _repo.ExecuteAsync(
            """INSERT INTO "authors" ("Id", "Name", "TenantId") VALUES (@Id, @Name, @TenantId)""",
            new { Id = idB, Name = "Bob", TenantId = "tenant-b" });

        var firstPage = (await entities.FetchKeysAndTenantsPagedAsync(SchemaBuilder.ToTableSchema(schema), afterKey: null, pageSize: 1)).ToList();
        firstPage.Should().HaveCount(1);
        firstPage[0].Key.Should().NotBeNullOrEmpty();
        firstPage[0].TenantId.Should().NotBeNullOrEmpty();

        var secondPage = (await entities.FetchKeysAndTenantsPagedAsync(SchemaBuilder.ToTableSchema(schema), afterKey: firstPage[0].Key, pageSize: 1)).ToList();
        secondPage.Should().HaveCount(1);
        secondPage[0].Key.Should().NotBe(firstPage[0].Key);

        var thirdPage = await entities.FetchKeysAndTenantsPagedAsync(SchemaBuilder.ToTableSchema(schema), afterKey: secondPage[0].Key, pageSize: 1);
        thirdPage.Should().BeEmpty();
    }
}
