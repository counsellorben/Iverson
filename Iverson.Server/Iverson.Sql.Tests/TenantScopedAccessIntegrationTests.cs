using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Iverson.Sql.Tests;

/// <summary>
/// Exercises the <c>tenantScoped</c>/<c>tenantId</c> role-switch plumbing (Part B1, Task 2) against
/// a real Postgres instance: that a tenant-scoped call actually runs as the non-superuser
/// <c>iverson_runtime</c> role and is therefore subject to RLS, rather than the app-level
/// <c>WHERE</c> clause being the only thing doing the filtering. Shares <see cref="PostgresContainerFixture"/>
/// with <see cref="PostgresIntegrationTests"/> (which already runs <c>EnsureRuntimeRoleAsync</c> on
/// fixture init).
/// </summary>
public sealed class TenantScopedAccessIntegrationTests(PostgresContainerFixture fixture)
    : IClassFixture<PostgresContainerFixture>
{
    private readonly PostgresRepository _repo = fixture.Repository;
    private readonly PostgresSchemaManager _schemaManager = fixture.SchemaManager;

    private static string UniqueTable() =>
        "tsa_" + Guid.NewGuid().ToString("N")[..8];

    private static TableSchema TenantScopedSchema(string table) => new(
        table,
        new ColumnSchema("id", "uuid", IsNullable: false),
        [
            new ColumnSchema("name", "text", IsNullable: false),
            new ColumnSchema("tenant_id", "text", IsNullable: false),
        ],
        TenantColumn: "tenant_id");

    private async Task<string> SeedTenantScopedTableAsync()
    {
        var table = UniqueTable();
        await _schemaManager.ApplySchemaAsync(TenantScopedSchema(table));

        // Written as the superuser `iverson` connection, which RLS never applies to — mirrors
        // today's write path and lets the test set up cross-tenant data unambiguously.
        await _repo.ExecuteAsync(
            $"INSERT INTO \"{table}\" (id, name, tenant_id) VALUES (@Id, @Name, @Tenant)",
            new { Id = Guid.NewGuid(), Name = "tenant-a-row", Tenant = "tenant-a" });
        await _repo.ExecuteAsync(
            $"INSERT INTO \"{table}\" (id, name, tenant_id) VALUES (@Id, @Name, @Tenant)",
            new { Id = Guid.NewGuid(), Name = "tenant-b-row", Tenant = "tenant-b" });

        return table;
    }

    [Fact]
    public async Task QueryAsync_TenantScopedTrue_WithTenantId_OnlySeesThatTenantsRows()
    {
        var table = await SeedTenantScopedTableAsync();

        var names = (await _repo.QueryAsync<string>(
            $"SELECT name FROM \"{table}\"", null, tenantScoped: true, tenantId: "tenant-a")).ToList();

        names.Should().ContainSingle().Which.Should().Be("tenant-a-row");
    }

    [Fact]
    public async Task QueryAsync_TenantScopedFalse_OnTheSameTable_SeesAllTenantsRows()
    {
        // Regression test for the tenantScoped/tenantId split: this must NOT collapse into a single
        // nullable parameter. tenantScoped:false is the unfiltered, unswitched (iverson-superuser)
        // path — proves it's genuinely a different code path from tenantScoped:true, tenantId:null below.
        var table = await SeedTenantScopedTableAsync();

        var names = (await _repo.QueryAsync<string>($"SELECT name FROM \"{table}\"")).ToList();

        names.Should().HaveCount(2);
    }

    [Fact]
    public async Task QueryAsync_TenantScopedTrue_WithNullTenantId_FailsClosed_ReturnsZeroRows()
    {
        // The explicit regression test for the tenantScoped/tenantId split itself: a tenant-scoped
        // caller whose claim happens to be missing must still switch roles (taking the transactional
        // iverson_runtime path) rather than skip the switch — set_config receives NULL, so RLS's
        // current_setting(..., true) = tenant_col predicate matches nothing. Zero rows, not an
        // exception and not the unfiltered result from the tenantScoped:false test above.
        var table = await SeedTenantScopedTableAsync();

        var names = (await _repo.QueryAsync<string>(
            $"SELECT name FROM \"{table}\"", null, tenantScoped: true, tenantId: null)).ToList();

        names.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_TenantScopedTrue_WithNullTenantId_FailsClosed_UpdatesZeroRows()
    {
        var table = await SeedTenantScopedTableAsync();

        var rows = await _repo.ExecuteAsync(
            $"UPDATE \"{table}\" SET name = 'changed'", null, tenantScoped: true, tenantId: null);

        rows.Should().Be(0);
    }

    [Fact]
    public async Task QuerySingleOrDefaultAsync_TenantScopedTrue_WithTenantId_OnlySeesThatTenantsRow()
    {
        var table = await SeedTenantScopedTableAsync();

        var name = await _repo.QuerySingleOrDefaultAsync<string>(
            $"SELECT name FROM \"{table}\" WHERE tenant_id = @Tenant",
            new { Tenant = "tenant-a" }, tenantScoped: true, tenantId: "tenant-a");

        name.Should().Be("tenant-a-row");

        var missing = await _repo.QuerySingleOrDefaultAsync<string>(
            $"SELECT name FROM \"{table}\" WHERE tenant_id = @Tenant",
            new { Tenant = "tenant-b" }, tenantScoped: true, tenantId: "tenant-a");

        missing.Should().BeNull();
    }

    [Fact]
    public async Task QueryAsync_TenantScopedTrue_OnLegacyNonTenantTable_ThrowsPermissionError_NotEmptyResult()
    {
        // A legacy table with no TenantColumn never gets an iverson_runtime GRANT (Task 1's
        // ApplySchemaAsync branch). Calling it tenant-scoped must surface a real Postgres
        // permission-denied error — proof that "no rows" elsewhere means RLS filtering, not that
        // iverson_runtime silently has no access to anything.
        var table = UniqueTable();
        await _schemaManager.ApplySchemaAsync(new TableSchema(
            table,
            new ColumnSchema("id", "uuid", IsNullable: false),
            [new ColumnSchema("name", "text", IsNullable: false)]));
        await _repo.ExecuteAsync(
            $"INSERT INTO \"{table}\" (id, name) VALUES (@Id, @Name)",
            new { Id = Guid.NewGuid(), Name = "some-row" });

        var act = async () => await _repo.QueryAsync<string>(
            $"SELECT name FROM \"{table}\"", null, tenantScoped: true, tenantId: "tenant-a");

        var thrown = await act.Should().ThrowAsync<PostgresException>();
        thrown.Which.SqlState.Should().Be("42501"); // insufficient_privilege
    }

    // ── OutboxWriter tenant-scoped upsert ─────────────────────────────────────

    [Fact]
    public async Task UpsertAndEnqueueOutboxAsync_WithTenantId_EntityVisibleUnderCorrectTenant_OutboxRowAlwaysPresent()
    {
        var entityTable = UniqueTable();
        var outboxTable = UniqueTable();
        await _schemaManager.ApplySchemaAsync(TenantScopedSchema(entityTable));
        // The outbox table itself is not tenant-scoped: the writer always runs its insert as the
        // superuser role (RESET ROLE before that statement), so a plain, ungranted table works.
        await _repo.ExecuteAsync($"""
            CREATE TABLE "{outboxTable}" (
                "Id" uuid PRIMARY KEY,
                "TypeName" text NOT NULL,
                "EntityKey" text NOT NULL,
                "EnqueuedAt" timestamptz NOT NULL,
                "Attempts" int NOT NULL,
                "LastError" text,
                "LastAttemptAt" timestamptz
            )
            """);

        var writer = new OutboxWriter(outboxTable, _repo, _repo);
        var id = Guid.NewGuid();
        var json = $$$"""{"id":"{{{id}}}","name":"tenant-a-row","tenant_id":"tenant-a"}""";

        var outboxRowId = await writer.UpsertAndEnqueueOutboxAsync(
            TenantScopedSchema(entityTable), "Widget", id.ToString(), json, tenantId: "tenant-a");

        outboxRowId.Should().NotBe(Guid.Empty);

        // Entity row visible as iverson_runtime for the matching tenant...
        var visible = await _repo.QuerySingleOrDefaultAsync<string>(
            $"SELECT name FROM \"{entityTable}\" WHERE id = @Id", new { Id = id },
            tenantScoped: true, tenantId: "tenant-a");
        visible.Should().Be("tenant-a-row");

        // ...but not for a different tenant (RLS actually filtering, not a no-op switch).
        var hiddenFromOtherTenant = await _repo.QuerySingleOrDefaultAsync<string>(
            $"SELECT name FROM \"{entityTable}\" WHERE id = @Id", new { Id = id },
            tenantScoped: true, tenantId: "tenant-b");
        hiddenFromOtherTenant.Should().BeNull();

        // Outbox row present regardless — its insert always runs at superuser privilege.
        var outboxCount = await _repo.QuerySingleOrDefaultAsync<int>(
            $"SELECT COUNT(*) FROM \"{outboxTable}\" WHERE \"Id\" = @Id", new { Id = outboxRowId });
        outboxCount.Should().Be(1);
    }

    // ── EntityRepository.DeleteAsync + plumbing-table write, one transaction ──

    [Fact]
    public async Task DeleteAsync_TenantScoped_ThenPlumbingTableInsert_InSameTransaction_Commits()
    {
        // Regression test for the Critical bug this fix closes: EntityRepository.DeleteAsync used
        // to switch to iverson_runtime (via SET LOCAL ROLE) for the tenant-scoped DELETE and never
        // reset it. Since SET LOCAL ROLE persists for the rest of the transaction, a subsequent
        // statement in the same transaction against a plumbing table with no iverson_runtime grant
        // (e.g. IversonReconciliationQueue, shaped here without a TenantColumn — mirrors
        // ObjectMappingGrpcService.Delete's real call sequence: EntityRepository.DeleteAsync
        // followed by OutboxWriter.EnqueueDeleteOutboxRowAsync in one ExecuteInTransactionAsync)
        // would fail with 42501 insufficient_privilege, rolling back the whole transaction — every
        // tenant-scoped entity delete failed at runtime. This proves the role is reset before the
        // plumbing insert, so the transaction commits.
        var entityTable = await SeedTenantScopedTableAsync();
        var plumbingTable = UniqueTable();
        // No TenantColumn: mirrors ReconciliationSchema.Table, which never gets an iverson_runtime
        // GRANT (Task 1's ApplySchemaAsync branch) — only the superuser `iverson` role can write here.
        await _schemaManager.ApplySchemaAsync(new TableSchema(
            plumbingTable,
            new ColumnSchema("id", "uuid", IsNullable: false),
            [new ColumnSchema("note", "text", IsNullable: false)]));

        var deletedId = await _repo.QuerySingleOrDefaultAsync<Guid>(
            $"SELECT id FROM \"{entityTable}\" WHERE tenant_id = @Tenant",
            new { Tenant = "tenant-a" });

        var entityRepo = new EntityRepository(_repo);
        var plumbingRowId = Guid.NewGuid();

        var act = async () => await _repo.ExecuteInTransactionAsync(async tx =>
        {
            await entityRepo.DeleteAsync(
                tx, TenantScopedSchema(entityTable), deletedId.ToString(),
                tenantScoped: true, tenantId: "tenant-a");

            // The plumbing-table write: must run as the superuser role, i.e. after the reset.
            await tx.ExecuteAsync(
                $"INSERT INTO \"{plumbingTable}\" (id, note) VALUES (@Id, @Note)",
                new { Id = plumbingRowId, Note = "delete-replay" });
        });

        await act.Should().NotThrowAsync();

        var remaining = await _repo.QuerySingleOrDefaultAsync<int>(
            $"SELECT COUNT(*) FROM \"{entityTable}\" WHERE id = @Id", new { Id = deletedId });
        remaining.Should().Be(0);

        var plumbingCount = await _repo.QuerySingleOrDefaultAsync<int>(
            $"SELECT COUNT(*) FROM \"{plumbingTable}\" WHERE id = @Id", new { Id = plumbingRowId });
        plumbingCount.Should().Be(1);
    }
}
