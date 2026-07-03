using FluentAssertions;
using Iverson.Api.Reconciliation;
using Iverson.Api.Schema;
using Iverson.Api.Tests.Helpers;
using Iverson.Events;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Testcontainers.PostgreSql;
using Xunit;

namespace Iverson.Api.Tests.Reconciliation;

/// <summary>
/// Exercises the reconciliation queue against a real Postgres instance (not a mocked
/// <see cref="IPostgresRepository"/>) specifically to catch the uuid/string type-mismatch class
/// of bug that mocked unit tests structurally cannot detect: Npgsql binding a native
/// <see cref="Guid"/> against a "uuid" column, and Dapper materializing a uuid column back into
/// <see cref="ReconciliationQueueRow.Id"/>.
/// </summary>
public sealed class ReconciliationQueuePostgresContainerFixture : IAsyncLifetime
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

public sealed class ReconciliationQueuePostgresIntegrationTests(ReconciliationQueuePostgresContainerFixture fixture)
    : IClassFixture<ReconciliationQueuePostgresContainerFixture>
{
    private readonly PostgresRepository _repo = fixture.Repository;

    [Fact]
    public async Task ProcessQueuedFailuresAsync_RoundTripsGuidQueueId_AgainstRealPostgres()
    {
        // ReconciliationSchema.TableName is a fixed constant (by design there is exactly one queue
        // table) — DROP/recreate at the start of this test so the fixture's real Postgres container
        // is left in a known-clean state regardless of test re-runs against the same container.
        await _repo.ExecuteAsync($"""DROP TABLE IF EXISTS "{ReconciliationSchema.TableName}" """);
        await _repo.ApplySchemaAsync(ReconciliationSchema.Table);

        // Real "authors" table + row, so ProcessOneAsync's entity re-fetch
        // (`WHERE "Id" = @Key::uuid`) has something real to find.
        var authorsTable = "authors_" + Guid.NewGuid().ToString("N")[..8];
        await _repo.ApplySchemaAsync(new TableSchema(
            authorsTable,
            new ColumnSchema("Id", "uuid", IsNullable: false),
            [new ColumnSchema("Name", "text", IsNullable: false)]));

        var authorId = Guid.NewGuid();
        await _repo.ExecuteAsync(
            $"""INSERT INTO "{authorsTable}" ("Id", "Name") VALUES (@Id, @Name)""",
            new { Id = authorId, Name = "Alice" });

        var schema = SchemaFixtures.AuthorSchema() with { TableName = authorsTable };
        var registry = new SchemaRegistry(_repo, NullLogger<SchemaRegistry>.Instance);
        await registry.RegisterAsync(schema);

        var events = Substitute.For<IEventProducer>();
        var sink = new PostgresFailedPublishSink(_repo);
        var service = new ReconciliationService(registry, _repo, events, NullLogger<ReconciliationService>.Instance);

        // 1+2: record a failed publish for this author — exercises the uuid-typed INSERT.
        var authorKey = authorId.ToString();
        await sink.RecordAsync("Author", authorKey, "broker unavailable");

        var queuedCountBefore = await _repo.QuerySingleOrDefaultAsync<int>(
            $"""SELECT COUNT(*) FROM "{ReconciliationSchema.TableName}" """);
        queuedCountBefore.Should().Be(1);

        // 3: process the queue for real — exercises the uuid-typed SELECT/UPDATE/DELETE and the
        // ::uuid-cast entity re-fetch.
        await service.ProcessQueuedFailuresAsync(CancellationToken.None);

        // 4: the queue row must be gone, and the entity must have been re-published exactly once.
        var queuedCountAfter = await _repo.QuerySingleOrDefaultAsync<int>(
            $"""SELECT COUNT(*) FROM "{ReconciliationSchema.TableName}" """);
        queuedCountAfter.Should().Be(0);

        await events.Received(1).ProduceAsync(
            EntityTopics.Updated,
            authorKey,
            Arg.Is<EntityEvent>(e =>
                e.TypeName == "Author" &&
                e.Key == authorKey &&
                e.PayloadJson.Contains("Alice")));
    }
}
