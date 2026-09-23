using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Iverson.Sql.Tests;

/// <summary>
/// Exercises the <see cref="RecordStoreRole"/> role-switch plumbing (Part B1, Task 2; re-cut for
/// CSR round-3 finding #5) against a real Postgres instance: that a tenant-scoped call actually
/// runs as the non-superuser, non-owning <c>iverson_runtime</c> role and is therefore subject to
/// RLS, rather than the app-level <c>WHERE</c> clause being the only thing doing the filtering —
/// and that the deliberately cross-tenant calls go through the <c>BYPASSRLS</c>
/// <c>iverson_maintenance</c> role rather than through whatever the connection happens to be.
/// Uses <see cref="PostgresContainerFixture"/> — via <c>IClassFixture</c>, so this class gets its
/// OWN instance and its own container, not one shared with <see cref="PostgresIntegrationTests"/>.
/// That isolation is load-bearing: the sibling file's EnsureRolesAsync tests deliberately strip
/// BYPASSRLS off iverson_maintenance mid-run, which would race these tests on a shared cluster.
/// The fixture runs <c>EnsureRolesAsync</c> on init.
/// </summary>
[Trait("Category", "Integration")]
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

        // Written on the container's own connection, which is a Postgres SUPERUSER — and
        // superusers bypass RLS unconditionally, FORCE or not. That is what makes this a usable
        // two-tenant setup step, and it is also the reason the acceptance test below has to
        // manufacture a non-superuser owner to observe FORCE ROW LEVEL SECURITY at all.
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
            $"SELECT name FROM \"{table}\"", null, RecordStoreRole.TenantRuntime, "tenant-a")).ToList();

        names.Should().ContainSingle().Which.Should().Be("tenant-a-row");
    }

    [Fact]
    public async Task QueryAsync_OnTheConnectionsOwnRole_SeesAllTenantsRows_WhichIsWhyNoEntityReadMayUseIt()
    {
        // Proves the role/tenantId split is genuinely two code paths (RecordStoreRole.Connection
        // vs TenantRuntime below), and — since CSR round-3 finding #5 — documents exactly why
        // EntityAccess offers no "connection role" value at all: on this container the connection
        // is a superuser and in kubernetes it is the tables' owner, and PostgreSQL exempts the
        // first unconditionally and the second unless the table is FORCEd. An entity read left on
        // this path returns every tenant's rows and never errors.
        //
        // RecordStoreRole.Connection survives only for the plumbing tables (outbox, DLQ, tenant
        // registry) that carry no policy and no grant for either entity role.
        var table = await SeedTenantScopedTableAsync();

        var names = (await _repo.QueryAsync<string>($"SELECT name FROM \"{table}\"")).ToList();

        names.Should().HaveCount(2);
    }

    [Fact]
    public async Task QueryAsync_Maintenance_OnATenantTable_SeesAllTenantsRows()
    {
        // The deliberate cross-tenant path (EntityAccess.CrossTenantMaintenance): reconciliation
        // replays and authoritative tenant/owner re-derivation. iverson_maintenance owns nothing,
        // so its visibility comes from BYPASSRLS and its GRANT — not from being the owner.
        var table = await SeedTenantScopedTableAsync();

        var names = (await _repo.QueryAsync<string>(
            $"SELECT name FROM \"{table}\"", null, RecordStoreRole.Maintenance)).ToList();

        names.Should().HaveCount(2);
    }

    [Fact]
    public async Task QueryAsync_Maintenance_OnANonTenantTable_Succeeds_NotPermissionDenied()
    {
        // The counterpart to QueryAsync_TenantScopedTrue_OnLegacyNonTenantTable below: a type that
        // declares no tenant column gets no policy and no iverson_runtime grant, but a
        // reconciliation replay of it is still a maintenance read — so ApplySchemaAsync grants
        // iverson_maintenance on EVERY table it manages, not just the tenant-scoped ones. Without
        // that unconditional grant this is 42501.
        var table = UniqueTable();
        await _schemaManager.ApplySchemaAsync(new TableSchema(
            table,
            new ColumnSchema("id", "uuid", IsNullable: false),
            [new ColumnSchema("name", "text", IsNullable: false)]));
        await _repo.ExecuteAsync(
            $"INSERT INTO \"{table}\" (id, name) VALUES (@Id, @Name)",
            new { Id = Guid.NewGuid(), Name = "some-row" });

        var names = (await _repo.QueryAsync<string>(
            $"SELECT name FROM \"{table}\"", null, RecordStoreRole.Maintenance)).ToList();

        names.Should().ContainSingle().Which.Should().Be("some-row");
    }

    // ── CSR round-3 finding #5 acceptance criterion ──────────────────────────

    [Fact]
    public async Task TableOwner_ReadingWithNoTenantScope_SeesZeroRows_UnderForcedRowLevelSecurity()
    {
        // THE acceptance test for CSR round-3 finding #5. Before the fix, tables got ENABLE ROW
        // LEVEL SECURITY but never FORCE, and the api connects as the tables' owner — and
        // PostgreSQL exempts a table's owner from its own policy unless FORCE is set. So a read
        // that forgot to scope itself returned EVERY tenant's rows, silently.
        //
        // The container's own connection is a superuser, and superusers bypass RLS regardless of
        // FORCE, so observing this needs a non-superuser OWNER — which is precisely the kubernetes
        // topology (CNPG `bootstrap.initdb.owner: iverson`, `enableSuperuserAccess: false`).
        // Manufacture one, hand it the table, and read with no app.tenant_id set.
        //
        // Delete the FORCE statement from PostgresSchemaManager and this sees 2 rows, not 0.
        var table = await SeedTenantScopedTableAsync();
        var owner = "owner_" + Guid.NewGuid().ToString("N")[..8];

        await _repo.ExecuteAsync($"CREATE ROLE \"{owner}\" NOLOGIN");
        await _repo.ExecuteAsync($"ALTER TABLE \"{table}\" OWNER TO \"{owner}\"");

        try
        {
            var visibleToOwner = await _repo.ExecuteInTransactionAsync(async tx =>
            {
                await tx.ExecuteAsync($"SET LOCAL ROLE \"{owner}\"");
                return await tx.QuerySingleOrDefaultAsync<int>($"SELECT COUNT(*) FROM \"{table}\"");
            });

            visibleToOwner.Should().Be(0);
        }
        finally
        {
            await _repo.ExecuteAsync($"ALTER TABLE \"{table}\" OWNER TO CURRENT_USER");
            await _repo.ExecuteAsync($"DROP ROLE \"{owner}\"");
        }
    }

    [Fact]
    public async Task ApplySchemaAsync_TenantScopedTable_SetsForceRowLevelSecurity()
    {
        // The catalogue-level assertion behind the behavioural one above: relforcerowsecurity, not
        // just relrowsecurity. A mutant that drops the FORCE statement fails here immediately.
        var table = await SeedTenantScopedTableAsync();

        var forced = await _repo.QuerySingleOrDefaultAsync<bool>(
            "SELECT relforcerowsecurity FROM pg_class WHERE relname = @Table", new { Table = table });

        forced.Should().BeTrue();
    }

    [Fact]
    public async Task QueryAsync_TenantScopedTrue_WithNullTenantId_FailsClosed_ReturnsZeroRows()
    {
        // The explicit regression test for the role/tenantId split itself: a tenant-scoped caller
        // whose claim happens to be missing must still switch roles (taking the transactional
        // iverson_runtime path) rather than skip the switch — set_config receives NULL, so RLS's
        // current_setting(..., true) = tenant_col predicate matches nothing. Zero rows, not an
        // exception and not the unfiltered result the connection role gives above.
        var table = await SeedTenantScopedTableAsync();

        var names = (await _repo.QueryAsync<string>(
            $"SELECT name FROM \"{table}\"", null, RecordStoreRole.TenantRuntime, null)).ToList();

        names.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_TenantScopedTrue_WithNullTenantId_FailsClosed_UpdatesZeroRows()
    {
        var table = await SeedTenantScopedTableAsync();

        var rows = await _repo.ExecuteAsync(
            $"UPDATE \"{table}\" SET name = 'changed'", null, RecordStoreRole.TenantRuntime, null);

        rows.Should().Be(0);
    }

    [Fact]
    public async Task QuerySingleOrDefaultAsync_TenantScopedTrue_WithTenantId_OnlySeesThatTenantsRow()
    {
        var table = await SeedTenantScopedTableAsync();

        var name = await _repo.QuerySingleOrDefaultAsync<string>(
            $"SELECT name FROM \"{table}\" WHERE tenant_id = @Tenant",
            new { Tenant = "tenant-a" }, RecordStoreRole.TenantRuntime, "tenant-a");

        name.Should().Be("tenant-a-row");

        var missing = await _repo.QuerySingleOrDefaultAsync<string>(
            $"SELECT name FROM \"{table}\" WHERE tenant_id = @Tenant",
            new { Tenant = "tenant-b" }, RecordStoreRole.TenantRuntime, "tenant-a");

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
            $"SELECT name FROM \"{table}\"", null, RecordStoreRole.TenantRuntime, "tenant-a");

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
            RecordStoreRole.TenantRuntime, "tenant-a");
        visible.Should().Be("tenant-a-row");

        // ...but not for a different tenant (RLS actually filtering, not a no-op switch).
        var hiddenFromOtherTenant = await _repo.QuerySingleOrDefaultAsync<string>(
            $"SELECT name FROM \"{entityTable}\" WHERE id = @Id", new { Id = id },
            RecordStoreRole.TenantRuntime, "tenant-b");
        hiddenFromOtherTenant.Should().BeNull();

        // Outbox row present regardless — its insert always runs at superuser privilege.
        var outboxCount = await _repo.QuerySingleOrDefaultAsync<int>(
            $"SELECT COUNT(*) FROM \"{outboxTable}\" WHERE \"Id\" = @Id", new { Id = outboxRowId });
        outboxCount.Should().Be(1);
    }

    [Fact]
    public async Task UpdateColumnsAsync_TenantScoped_ThenPlumbingTableInsert_InSameTransaction_Commits()
    {
        // The DeleteAsync sibling below, for the writeback path. UpdateColumnsAsync took over the
        // role enter/exit from EnrichmentConsumer (fix round 1, low item 1), so the "reset before
        // the plumbing write" guarantee now has to hold inside the repository — against a real
        // Postgres, not a mocked transaction context. EnrichmentConsumer's real sequence is
        // UpdateColumnsAsync, then the enrichment-state upsert, then the outbox insert, all in one
        // ExecuteInTransactionAsync, and iverson_runtime has a grant on none of the latter.
        var entityTable = await SeedTenantScopedTableAsync();
        var plumbingTable = UniqueTable();
        await _schemaManager.ApplySchemaAsync(new TableSchema(
            plumbingTable,
            new ColumnSchema("id", "uuid", IsNullable: false),
            [new ColumnSchema("note", "text", IsNullable: false)]));

        var updatedId = await _repo.QuerySingleOrDefaultAsync<Guid>(
            $"SELECT id FROM \"{entityTable}\" WHERE tenant_id = @Tenant", new { Tenant = "tenant-a" });

        var entityRepo = new EntityRepository(_repo);
        var plumbingRowId = Guid.NewGuid();

        var act = async () => await _repo.ExecuteInTransactionAsync(async tx =>
        {
            await entityRepo.UpdateColumnsAsync(
                tx, TenantScopedSchema(entityTable), updatedId.ToString(),
                new Dictionary<string, object?> { ["name"] = "enriched" },
                EntityAccess.ForTenant("tenant-a"));

            await tx.ExecuteAsync(
                $"INSERT INTO \"{plumbingTable}\" (id, note) VALUES (@Id, @Note)",
                new { Id = plumbingRowId, Note = "enrichment-state" });
        });

        await act.Should().NotThrowAsync();

        var updatedName = await _repo.QuerySingleOrDefaultAsync<string>(
            $"SELECT name FROM \"{entityTable}\" WHERE id = @Id", new { Id = updatedId });
        updatedName.Should().Be("enriched");

        var plumbingCount = await _repo.QuerySingleOrDefaultAsync<int>(
            $"SELECT COUNT(*) FROM \"{plumbingTable}\" WHERE id = @Id", new { Id = plumbingRowId });
        plumbingCount.Should().Be(1);
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
            await entityRepo.DeleteAsync(tx, TenantScopedSchema(entityTable), deletedId.ToString(), EntityAccess.ForTenant("tenant-a"));

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
