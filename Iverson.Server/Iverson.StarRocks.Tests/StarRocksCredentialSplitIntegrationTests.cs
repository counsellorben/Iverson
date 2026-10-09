using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Xunit;

namespace Iverson.StarRocks.Tests;

/// <summary>
/// CSR round 11 §3: the StarRocks credential split, against a live engine. The constructor first gives
/// iverson_app the grants that earlier versions of job-create-user.yaml gave it, then applies the current
/// job's statements through <see cref="StarRocksSplitLogins"/>, so every test here sees an upgraded
/// install: the revokes have something to remove. Other classes in the collection call the same helper,
/// so whichever class runs last leaves the logins in the same state.
/// </summary>
[Trait("Category", "Integration")]
[Collection(StarRocksCollection.Name)]
public sealed class StarRocksCredentialSplitIntegrationTests
{
    private readonly StarRocksContainerFixture _fx;
    private readonly string _appConnectionString;
    private readonly EngagementRepository _repo;

    public StarRocksCredentialSplitIntegrationTests(StarRocksContainerFixture fx)
    {
        _fx = fx;

        // The grant set job-create-user.yaml issued to iverson_app before the split. The password matches
        // StarRocksSplitLogins'; CREATE USER IF NOT EXISTS leaves an existing user's password alone.
        fx.Repository.ExecuteAsync($"""
            CREATE USER IF NOT EXISTS 'iverson_app'@'%' IDENTIFIED BY '{StarRocksSplitLogins.AppPassword}';
            CREATE DATABASE IF NOT EXISTS iverson;
            GRANT SELECT, INSERT, UPDATE, DELETE ON iverson.* TO 'iverson_app'@'%';
            GRANT CREATE TABLE, DROP, ALTER ON DATABASE iverson TO 'iverson_app'@'%';
            GRANT OPERATE ON SYSTEM TO 'iverson_app'@'%';
            GRANT CREATE DATABASE ON CATALOG default_catalog TO 'iverson_app'@'%';
            GRANT user_admin TO 'iverson_app'@'%';
            """).GetAwaiter().GetResult();

        (_appConnectionString, var provisionerConnectionString) =
            StarRocksSplitLogins.EnsureAsync(fx).GetAwaiter().GetResult();

        _repo = new EngagementRepository(_appConnectionString, NullLogger<EngagementRepository>.Instance,
            provisionerConnectionString: provisionerConnectionString);
    }

    private static string UniqueTenantId() => "t" + Guid.NewGuid().ToString("N")[..16];
    private static string UniqueTable() => "tbl_" + Guid.NewGuid().ToString("N")[..8];

    private static EngagementTableSchema ProbeSchema(string tableName) =>
        new(tableName, new EngagementColumnSchema("Id", "VARCHAR(36)", false),
            [new EngagementColumnSchema("Name", "STRING", true)]);

    private static string ProbePayload(string id, string name) =>
        System.Text.Json.JsonSerializer.Serialize(new { Id = id, Name = name });

    private async Task<List<dynamic>> SearchAsTenantAsync(string table, string tenantId) =>
        (await _repo.SearchAsync(new EngagementQuerySchema("Probe", table, "Id", ["Name"]), null, 0, 50,
            authz: new Dictionary<string, AuthorizationConstraint>
            {
                ["Probe"] = new AuthorizationConstraint(null, null, null, TenantValue: tenantId)
            })).ToList();

    /// <summary>Opens a connection as iverson_app and proves the login itself works, so a later refusal is
    /// about the statement, not about connecting.</summary>
    private async Task<MySqlConnection> OpenAppConnectionAsync()
    {
        var conn = new MySqlConnection(_appConnectionString);
        await conn.OpenAsync();
        (await conn.QueryAsync<int>("SELECT 1")).Should().ContainSingle().Which.Should().Be(1);
        return conn;
    }

    /// <summary>
    /// What the query login's own provisioning left behind before the split: the database, the role and
    /// its grants, the role granted to iverson_app, and one table. iverson_provisioner holds none of it.
    /// </summary>
    private async Task CreateTenantAsBeforeTheSplitAsync(string tenantId, EngagementTableSchema schema)
    {
        var db = TenantIdentifier.DatabaseName(tenantId);
        var role = TenantIdentifier.RoleName(tenantId);
        await _fx.Repository.ExecuteAsync($"""
            CREATE DATABASE IF NOT EXISTS `{db}`;
            CREATE ROLE IF NOT EXISTS `{role}`;
            GRANT SELECT, INSERT, UPDATE, DELETE ON `{db}`.* TO ROLE `{role}`;
            GRANT CREATE TABLE ON DATABASE `{db}` TO ROLE `{role}`;
            GRANT `{role}` TO USER 'iverson_app'@'%';
            """);
        await _fx.Repository.ExecuteAsync(
            StarRocksSchemaManager.BuildCreateTableDdl(schema, TenantIdentifier.Qualify(db, schema.TableName)));
    }

    private async Task<long?> ColumnMaxLengthAsync(string tenantId, string table, string column) =>
        (await _fx.Repository.QueryAsync<long?>(
            """
            SELECT CHARACTER_MAXIMUM_LENGTH FROM information_schema.columns
            WHERE TABLE_SCHEMA = @Db AND TABLE_NAME = @Table AND COLUMN_NAME = @Column
            """,
            new { Db = TenantIdentifier.DatabaseName(tenantId), Table = table, Column = column })).Single();

    // MODIFY COLUMN can run as a StarRocks schema-change job, so the new width may appear shortly after
    // the statement returns.
    private async Task<long?> WaitForColumnMaxLengthAsync(string tenantId, string table, string column, long expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        long? current;
        while ((current = await ColumnMaxLengthAsync(tenantId, table, column)) != expected && DateTime.UtcNow < deadline)
            await Task.Delay(TimeSpan.FromSeconds(2));
        return current;
    }

    private static EngagementTableSchema BodySchema(string table, string bodyType) =>
        new(table, new EngagementColumnSchema("Id", "VARCHAR(36)", false),
            [new EngagementColumnSchema("Body", bodyType, true)]);

    // ── what the query login can no longer do ───────────────────────────────────────────────────

    [Fact]
    public async Task AppLogin_CannotCreateADatabase()
    {
        await using var conn = await OpenAppConnectionAsync();

        var act = () => conn.ExecuteAsync($"CREATE DATABASE `split_probe_{Guid.NewGuid():N}`");

        await act.Should().ThrowAsync<MySqlException>().WithMessage("*Access denied*");
    }

    [Fact]
    public async Task AppLogin_CannotActivateUserAdmin()
    {
        await using var conn = await OpenAppConnectionAsync();

        var act = () => conn.ExecuteAsync("SET ROLE user_admin");

        await act.Should().ThrowAsync<MySqlException>().WithMessage("*is not granted to*");
    }

    [Fact]
    public async Task AppLogin_CannotReadTheSharedDatabase()
    {
        var table = UniqueTable();
        await _fx.Repository.ExecuteAsync(
            StarRocksSchemaManager.BuildCreateTableDdl(ProbeSchema(table), $"`iverson`.`{table}`"));
        await _fx.Repository.ExecuteAsync(
            $"INSERT INTO `iverson`.`{table}` VALUES ('10000000-0000-0000-0000-000000000001', 'Shared')");
        await using var conn = await OpenAppConnectionAsync();

        var act = () => conn.QueryAsync($"SELECT * FROM `iverson`.`{table}`");

        await act.Should().ThrowAsync<MySqlException>().WithMessage("*Access denied*");
    }

    // ── what it still does ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AppLogin_PassesTheReadinessGateAndTheHealthCheck()
    {
        // A fresh repository: its first operation goes through the cold-start readiness gate (SHOW BACKENDS,
        // no role active), which needs the one direct grant iverson_app keeps.
        var repo = new EngagementRepository(_appConnectionString, NullLogger<EngagementRepository>.Instance,
            new EngagementResilienceOptions { BackendReadyTimeout = TimeSpan.FromSeconds(30) });

        (await repo.QueryAsync<int>("SELECT 1")).Should().ContainSingle().Which.Should().Be(1);
        (await new EngagementHealthChecker(_appConnectionString).CheckHealthAsync())
            .Should().Be(EngagementHealthStatus.Healthy);
    }

    // ── what the provisioner does ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Provisioner_ProvisionsANewTenant_ThatTheAppLoginCanUse()
    {
        var tenantId = UniqueTenantId();
        var table = UniqueTable();
        var schema = ProbeSchema(table);

        await _repo.EnsureTenantProvisionedAsync(tenantId, schema);
        await _repo.UpsertAsync(schema, ProbePayload("20000000-0000-0000-0000-000000000001", "New"), tenantId);

        var rows = await SearchAsTenantAsync(table, tenantId);
        rows.Should().ContainSingle();
        ((string)rows[0].Name).Should().Be("New");
    }

    [Fact]
    public async Task Provisioner_ReprovisionsATenantCreatedBeforeTheSplit()
    {
        var tenantId = UniqueTenantId();
        var oldTable = UniqueTable();
        await CreateTenantAsBeforeTheSplitAsync(tenantId, ProbeSchema(oldTable));
        await _fx.Repository.ExecuteAsync(
            $"INSERT INTO {TenantIdentifier.Qualify(TenantIdentifier.DatabaseName(tenantId), oldTable)} " +
            "VALUES ('30000000-0000-0000-0000-000000000001', 'BeforeSplit')");

        // A new type for the existing tenant: provisioning must activate the tenant role, which only the
        // provisioner's own grant of that role to itself makes possible.
        var newTable = UniqueTable();
        await _repo.EnsureTenantProvisionedAsync(tenantId, ProbeSchema(newTable));
        await _repo.UpsertAsync(ProbeSchema(newTable),
            ProbePayload("30000000-0000-0000-0000-000000000002", "AfterSplit"), tenantId);

        var oldRows = await SearchAsTenantAsync(oldTable, tenantId);
        var newRows = await SearchAsTenantAsync(newTable, tenantId);
        oldRows.Should().ContainSingle();
        ((string)oldRows[0].Name).Should().Be("BeforeSplit");
        newRows.Should().ContainSingle();
        ((string)newRows[0].Name).Should().Be("AfterSplit");
    }

    [Fact]
    public async Task Provisioner_WidensALargeTextColumn_LeftNarrowByAnEarlierProvisioning()
    {
        var tenantId = UniqueTenantId();
        var table = UniqueTable();

        // The first call creates the table with the narrower column. The second asks for the wide type,
        // which CREATE TABLE IF NOT EXISTS leaves alone, so only the widening step can change it.
        await _repo.EnsureTenantProvisionedAsync(tenantId, BodySchema(table, "VARCHAR(65533)"));
        (await ColumnMaxLengthAsync(tenantId, table, "Body")).Should().Be(StarRocksLimits.StringAliasBytes);

        await _repo.EnsureTenantProvisionedAsync(tenantId, BodySchema(table, StarRocksLimits.WideTextColumnType));

        (await WaitForColumnMaxLengthAsync(tenantId, table, "Body", StarRocksLimits.MaxVarcharBytes))
            .Should().Be(StarRocksLimits.MaxVarcharBytes);
    }

    [Fact]
    public async Task Provisioner_WidensALargeTextColumn_OnATableCreatedBeforeTheSplit()
    {
        // The case widening exists for: a table from an older deployment, which predates the provisioner's
        // ALTER grant.
        var tenantId = UniqueTenantId();
        var table = UniqueTable();
        await CreateTenantAsBeforeTheSplitAsync(tenantId, BodySchema(table, "VARCHAR(65533)"));
        (await ColumnMaxLengthAsync(tenantId, table, "Body")).Should().Be(StarRocksLimits.StringAliasBytes);

        await _repo.EnsureTenantProvisionedAsync(tenantId, BodySchema(table, StarRocksLimits.WideTextColumnType));

        (await WaitForColumnMaxLengthAsync(tenantId, table, "Body", StarRocksLimits.MaxVarcharBytes))
            .Should().Be(StarRocksLimits.MaxVarcharBytes);
    }
}
