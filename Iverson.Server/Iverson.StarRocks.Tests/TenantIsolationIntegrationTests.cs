using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Xunit;

namespace Iverson.StarRocks.Tests;

// Proves the actual privilege boundary Tasks 1-6 built: per-tenant StarRocks databases + roles,
// exercised through a second StarRocksRepository that authenticates as the real application user
// (`iverson_app`) rather than `root` (StarRocksContainerFixture.Repository). Application-level
// WHERE-clause tenant filtering is Part A's job and is deliberately NOT exercised here (every
// AuthorizationConstraint below leaves TenantColumn null) — these tests must fail if the
// database/role/grant boundary itself regresses, even if a WHERE clause would have masked it.
[Trait("Category", "Integration")]
public sealed class TenantIsolationIntegrationTests : IClassFixture<StarRocksContainerFixture>
{
    private readonly StarRocksRepository _appRepo;
    private readonly StarRocksRepository _rootRepo;

    public TenantIsolationIntegrationTests(StarRocksContainerFixture fx)
    {
        _rootRepo = fx.Repository;

        // fx.Repository connects as root — reuse it to create iverson_app + user_admin, mirroring
        // job-create-user.yaml. Idempotent (IF NOT EXISTS / re-GRANT), safe if xUnit re-runs this
        // constructor per test in the class.
        //
        // OPERATE ON SYSTEM is granted directly to the user, not via a role: StarRocks does not
        // auto-activate granted roles on a fresh connection (a new session's active role is NONE
        // until an explicit SET ROLE), but StarRocksRepository's cold-start readiness gate calls
        // SHOW BACKENDS on every new StarRocksRepository instance before any role has been
        // activated — exactly the gap job-create-user.yaml's direct (non-role) grant closes in
        // production. Discovered empirically: the brief's illustrative user_admin-only grant left
        // every _appRepo operation failing readiness with "Access denied ... OPERATE OR NODE
        // privilege(s) on SYSTEM ... Current role(s): NONE".
        // CREATE DATABASE ON CATALOG default_catalog is also granted directly (not via a role):
        // StarRocks's built-in user_admin role covers user/role/grant management but not schema
        // DDL — EnsureTenantProvisionedAsync's `CREATE DATABASE IF NOT EXISTS` (run under SET
        // ROLE user_admin) fails with "Access denied ... CREATE DATABASE privilege(s) on CATALOG
        // default_catalog ... Current role(s): [user_admin]" without it. job-create-user.yaml
        // grants this to production's iverson_app for the identical reason (EnsureDatabaseAsync).
        fx.Repository.ExecuteAsync("""
            CREATE USER IF NOT EXISTS 'iverson_app'@'%' IDENTIFIED BY 'test_pw';
            GRANT user_admin TO 'iverson_app'@'%';
            GRANT OPERATE ON SYSTEM TO 'iverson_app'@'%';
            GRANT CREATE DATABASE ON CATALOG default_catalog TO 'iverson_app'@'%';
            """).GetAwaiter().GetResult();

        // Database deliberately cleared, not inherited from fx.ConnectionString: this test's
        // iverson_app only holds the user_admin ROLE, and StarRocks never auto-activates a
        // granted role on a fresh connection (active role is NONE until an explicit SET ROLE) —
        // unlike production, where job-create-user.yaml also grants direct, non-role privileges
        // on the shared database, letting a connection select it as its default at handshake
        // time. Without a matching direct grant here, setting Database=iverson_test on this
        // connection string made every single connection attempt fail immediately with
        // MySqlEndOfStreamException ("incomplete response") during the handshake — confirmed by
        // isolated repro, not resource contention. Every real operation under test below is
        // either fully qualified (`db`.`table`, via TenantIdentifier.Qualify) or runs under an
        // explicit SET ROLE after connecting, so the connection itself never needs a default
        // database to succeed.
        var appConnectionString = new MySqlConnectionStringBuilder(fx.ConnectionString)
        {
            UserID = "iverson_app",
            Password = "test_pw",
            Database = ""
        }.ToString();

        _appRepo = new StarRocksRepository(appConnectionString, NullLogger<StarRocksRepository>.Instance);
    }

    private static StarRocksTableSchema ProbeSchema(string tableName) =>
        new(tableName, new StarRocksColumnSchema("Id", "VARCHAR(36)", false),
            [new StarRocksColumnSchema("Name", "STRING", true)]);

    // Unique per call so tests that provision a tenant/table never collide with each other, even
    // though (per xUnit's default sequential-within-a-class execution) they share one container.
    private static string UniqueTenantId() => "t" + Guid.NewGuid().ToString("N")[..16];
    private static string UniqueTable() => "tbl_" + Guid.NewGuid().ToString("N")[..8];

    private static string ProbePayload(string id, string name) =>
        JsonSerializer.Serialize(new { Id = id, Name = name });

    // TenantColumn deliberately left null throughout this file: the point of these tests is to
    // prove the SET ROLE / GRANT boundary (Tasks 2-6) blocks cross-tenant reads on its own, not
    // that StarRocksQueryBuilder's application-level WHERE-clause tenant filter (Part A) does.
    private static Dictionary<string, AuthorizationConstraint> AuthzFor(string typeName, string tenantId) =>
        new() { [typeName] = new AuthorizationConstraint(null, null, null, TenantValue: tenantId) };

    [Fact]
    public async Task EnsureTenantProvisionedAsync_ThenUpsert_SucceedsForOwnTenant()
    {
        var tenantId = UniqueTenantId();
        var table = UniqueTable();
        var schema = ProbeSchema(table);

        await _appRepo.EnsureTenantProvisionedAsync(tenantId, schema);
        await _appRepo.UpsertAsync(schema, ProbePayload("11111111-1111-1111-1111-111111111111", "Alice"), tenantId);

        var querySchema = new StarRocksQuerySchema("Probe", table, "Id", ["Name"]);
        var rows = (await _appRepo.SearchAsync(querySchema, null, 0, 50, authz: AuthzFor("Probe", tenantId))).ToList();

        rows.Should().ContainSingle();
        ((string)rows[0].Name).Should().Be("Alice");
    }

    [Fact]
    public async Task SearchAsync_ForOtherTenant_ReturnsNoRows()
    {
        var tenantA = UniqueTenantId();
        var tenantB = UniqueTenantId(); // never provisioned
        var table = UniqueTable();
        var schema = ProbeSchema(table);

        await _appRepo.EnsureTenantProvisionedAsync(tenantA, schema);
        await _appRepo.UpsertAsync(schema, ProbePayload("22222222-2222-2222-2222-222222222222", "Bob"), tenantA);

        var querySchema = new StarRocksQuerySchema("Probe", table, "Id", ["Name"]);

        // Tenant b was never provisioned, so role_tenant_<b> doesn't exist and was never granted
        // to iverson_app — `SET ROLE` on an unknown/ungranted role fails at the connection level
        // inside RunTenantScopedAsync, before any SELECT against iverson_tenant_<a> is possible.
        // That failure (not an empty WHERE-filtered result set) *is* the privilege boundary this
        // scenario proves, so the correct observable behavior is a thrown exception, not [].
        var act = async () => await _appRepo.SearchAsync(querySchema, null, 0, 50, authz: AuthzFor("Probe", tenantB));

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task EnsureTenantProvisionedAsync_CalledTwice_IsIdempotent()
    {
        var tenantId = UniqueTenantId();
        var table = UniqueTable();
        var schema = ProbeSchema(table);

        var act = async () =>
        {
            await _appRepo.EnsureTenantProvisionedAsync(tenantId, schema);
            await _appRepo.EnsureTenantProvisionedAsync(tenantId, schema);
        };

        await act.Should().NotThrowAsync();

        // And the tenant must still be fully usable afterward — idempotency that left the
        // database/role/table in a broken half-state would defeat the point of this test.
        await _appRepo.UpsertAsync(schema, ProbePayload("33333333-3333-3333-3333-333333333333", "Carol"), tenantId);
        var querySchema = new StarRocksQuerySchema("Probe", table, "Id", ["Name"]);
        var rows = (await _appRepo.SearchAsync(querySchema, null, 0, 50, authz: AuthzFor("Probe", tenantId))).ToList();
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task EnsureTenantProvisionedAsync_ThenSecondType_WildcardGrantCoversNewTable()
    {
        var tenantId = UniqueTenantId();
        var table1 = UniqueTable();
        var table2 = UniqueTable();
        var schema1 = ProbeSchema(table1);
        var schema2 = ProbeSchema(table2);

        // First call creates the database, role, and the db-wide `GRANT ... ON db.* TO ROLE`
        // (Task 6's wildcard grant). The second call provisions a brand-new table under the
        // *same* tenant/role — if the wildcard grant didn't actually cover future tables, this
        // second type would be unreadable by role_tenant_<id> despite provisioning "succeeding".
        await _appRepo.EnsureTenantProvisionedAsync(tenantId, schema1);
        await _appRepo.EnsureTenantProvisionedAsync(tenantId, schema2);

        await _appRepo.UpsertAsync(schema1, ProbePayload("44444444-4444-4444-4444-444444444444", "One"), tenantId);
        await _appRepo.UpsertAsync(schema2, ProbePayload("55555555-5555-5555-5555-555555555555", "Two"), tenantId);

        var query1 = new StarRocksQuerySchema("Probe1", table1, "Id", ["Name"]);
        var query2 = new StarRocksQuerySchema("Probe2", table2, "Id", ["Name"]);

        var rows1 = (await _appRepo.SearchAsync(query1, null, 0, 50, authz: AuthzFor("Probe1", tenantId))).ToList();
        var rows2 = (await _appRepo.SearchAsync(query2, null, 0, 50, authz: AuthzFor("Probe2", tenantId))).ToList();

        rows1.Should().ContainSingle();
        ((string)rows1[0].Name).Should().Be("One");
        rows2.Should().ContainSingle();
        ((string)rows2[0].Name).Should().Be("Two");
    }

    [Fact]
    public async Task UpsertAsync_WithInvalidTenantId_IsNoOp()
    {
        var tenantId = UniqueTenantId();
        var table = UniqueTable();
        var schema = ProbeSchema(table);
        await _appRepo.EnsureTenantProvisionedAsync(tenantId, schema);

        // Fails TenantIdentifier.IsValid (contains a backtick, which would otherwise break out of
        // the identifier quoting used to build `iverson_tenant_<id>` / `role_tenant_<id>`).
        const string invalidTenantId = "bad`tenant";

        var act = async () => await _appRepo.UpsertAsync(
            schema, ProbePayload("66666666-6666-6666-6666-666666666666", "Malicious"), invalidTenantId);

        await act.Should().NotThrowAsync();

        // Prove no row actually landed anywhere reachable: the legitimately provisioned tenant's
        // table (the only real table named `table` that a valid write could plausibly have hit)
        // must still be empty.
        var querySchema = new StarRocksQuerySchema("Probe", table, "Id", ["Name"]);
        var rows = (await _appRepo.SearchAsync(querySchema, null, 0, 50, authz: AuthzFor("Probe", tenantId))).ToList();
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_WithAuthzNull_UsesUnscopedSharedDatabase()
    {
        // Locks in the Task 2 Step 4 resolution: authz: null means "no tenant scoping was
        // requested" (e.g. a caller that predates/bypasses Part A's authorization evaluation),
        // which SearchAsync must treat differently from "authz was provided but denied" (which
        // returns [] — see SearchAsync_ForOtherTenant_ReturnsNoRows and the invalid-tenant test
        // above). A regression that collapsed these two cases would make this query wrongly
        // return no rows instead of actually executing against the shared/default database.
        //
        // Uses _rootRepo, not _appRepo: _appRepo's connection string deliberately has no default
        // database (see constructor remarks), so it cannot run the unqualified DDL/DML this
        // scenario needs. _rootRepo (root, default database = iverson_test, set up by
        // StarRocksContainerFixture) exercises the exact same StarRocksRepository.SearchAsync
        // code path and authz-null branch — that shared code path, not the calling user's
        // identity, is what this test is proving.
        var table = UniqueTable();
        var schema = ProbeSchema(table);

        // Unqualified DDL/DML — lands in the connection's default database (the shared,
        // non-tenant-scoped `iverson_test` database StarRocksContainerFixture created), exactly
        // like pre-tenant-boundary callers that never pass authz at all.
        await _rootRepo.ExecuteAsync(StarRocksSchemaManager.BuildCreateTableDdl(schema, $"`{table}`"));
        await _rootRepo.ExecuteAsync($"INSERT INTO `{table}` VALUES ('77777777-7777-7777-7777-777777777777', 'Unscoped')");

        var querySchema = new StarRocksQuerySchema("Probe", table, "Id", ["Name"]);
        var rows = (await _rootRepo.SearchAsync(querySchema, null, 0, 50, authz: null)).ToList();

        rows.Should().ContainSingle();
        ((string)rows[0].Name).Should().Be("Unscoped");
    }
}
