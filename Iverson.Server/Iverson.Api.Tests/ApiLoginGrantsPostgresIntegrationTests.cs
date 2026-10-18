using Dapper;
using FluentAssertions;
using Iverson.Api.PrivilegedRequests;
using Iverson.Api.Reconciliation;
using Iverson.Api.Tenancy;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Iverson.Api.Tests;

/// <summary>
/// The worker's startup against a real Postgres. <see cref="WorkerStoreBootstrap"/> creates the
/// plumbing tables and gives the api's login its grants, exactly as Program.cs runs it. The
/// container's own login is a superuser, standing in for the owner. The password path, which
/// creates the login, is compose's.
/// </summary>
public sealed class ApiLoginGrantsPostgresContainerFixture : IAsyncLifetime
{
    public const string ApiPassword = "api-login-test-only";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine")
        .Build();

    public string OwnerConnectionString => _container.GetConnectionString();
    public string ApiConnectionString { get; private set; } = null!;
    public PostgresRepository Owner { get; private set; } = null!;
    public PostgresSchemaManager OwnerSchemaManager { get; private set; } = null!;
    public PostgresRepository Api { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Owner = new PostgresRepository(OwnerConnectionString, NullLogger<PostgresRepository>.Instance);
        OwnerSchemaManager = new PostgresSchemaManager(OwnerConnectionString, NullLogger<PostgresSchemaManager>.Instance);

        await WorkerStoreBootstrap.RunAsync(
            OwnerSchemaManager,
            new SchemaRegistryRepository(Owner),
            new EnrichmentStateRepository(Owner),
            new DocumentRerenderQueueRepository(Owner),
            new TokenRevocationRepository(Owner),
            ApiPassword,
            TimeSpan.Zero);

        ApiConnectionString = new NpgsqlConnectionStringBuilder(OwnerConnectionString)
        {
            Username = PostgresSchemaManager.ApiLoginName,
            Password = ApiPassword
        }.ConnectionString;
        Api = new PostgresRepository(ApiConnectionString, NullLogger<PostgresRepository>.Instance);
    }

    /// <summary>The worker's grant step again, with the password: recreates the login if a test dropped it.</summary>
    public Task RestoreApiLoginAsync() =>
        OwnerSchemaManager.EnsureApiLoginAsync(ApiPassword, ApiLoginGrants.Statements, ApiLoginGrants.Tables, TimeSpan.Zero);

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

/// <summary>
/// CSR round 12 §6 and §9: the api's repositories run as a login that is not the owner, holds no
/// BYPASSRLS, and has exactly the §6.2 grants. Every store call the api makes succeeds, and reading
/// outbox payloads or <c>MessageValue</c>, entering <c>iverson_maintenance</c>, and DDL are each
/// denied.
/// </summary>
[Trait("Category", "Integration")]
[Collection(ContainerCollection.Name)]
public sealed class ApiLoginGrantsPostgresIntegrationTests(ApiLoginGrantsPostgresContainerFixture fixture)
    : IClassFixture<ApiLoginGrantsPostgresContainerFixture>
{
    private const string TenantColumn = "__TenantId";
    private const string PrivilegedRequestsTable = "IversonPrivilegedRequests";

    private readonly PostgresRepository _owner = fixture.Owner;
    private readonly PostgresRepository _api = fixture.Api;

    private static TableSchema WidgetSchema(string table) => new(
        table,
        new ColumnSchema("Id", "uuid", IsNullable: false),
        [
            new ColumnSchema("Name", "text", IsNullable: false),
            new ColumnSchema("ParentId", "uuid", IsNullable: true),
            new ColumnSchema(TenantColumn, "text", IsNullable: false),
        ],
        TenantColumn: TenantColumn);

    private static async Task DeniedAsync(Func<Task> act) =>
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");

    private string ConnectionStringFor(string login, string password) =>
        new NpgsqlConnectionStringBuilder(fixture.OwnerConnectionString) { Username = login, Password = password }
            .ConnectionString;

    [Fact]
    public async Task ApiLogin_OwnsNothing_HoldsNoBypassRls_AndIsAMemberOfTheRuntimeRoleOnly()
    {
        var attributes = await _owner.QuerySingleOrDefaultAsync<(bool Super, bool BypassRls, bool CreateRole, bool CreateDb)>(
            "SELECT rolsuper, rolbypassrls, rolcreaterole, rolcreatedb FROM pg_roles WHERE rolname = 'iverson_api'");

        attributes.Super.Should().BeFalse();
        attributes.BypassRls.Should().BeFalse();
        attributes.CreateRole.Should().BeFalse();
        attributes.CreateDb.Should().BeFalse();
        (await _owner.QuerySingleOrDefaultAsync<bool>(
            "SELECT pg_has_role('iverson_api', 'iverson_runtime', 'MEMBER')")).Should().BeTrue();
        (await _owner.QuerySingleOrDefaultAsync<bool>(
            "SELECT pg_has_role('iverson_api', 'iverson_maintenance', 'MEMBER')")).Should().BeFalse();
        (await _owner.QuerySingleOrDefaultAsync<long>(
            "SELECT count(*) FROM pg_class WHERE relowner = 'iverson_api'::regrole")).Should().Be(0);
    }

    [Fact]
    public async Task ApiLogin_HoldsExactlyTheListedPrivileges()
    {
        var tablePrivileges = await _owner.QueryAsync<string>(
            """
            SELECT c.relname || ':' || a.privilege_type
            FROM pg_class c CROSS JOIN LATERAL aclexplode(c.relacl) a
            WHERE a.grantee = 'iverson_api'::regrole
            """);
        var columnPrivileges = await _owner.QueryAsync<string>(
            """
            SELECT c.relname || '.' || att.attname || ':' || a.privilege_type
            FROM pg_attribute att
            JOIN pg_class c ON c.oid = att.attrelid
            CROSS JOIN LATERAL aclexplode(att.attacl) a
            WHERE a.grantee = 'iverson_api'::regrole
            """);
        var connect = await _owner.QuerySingleOrDefaultAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1 FROM pg_database d CROSS JOIN LATERAL aclexplode(d.datacl) a
                WHERE d.datname = current_database()
                  AND a.grantee = 'iverson_api'::regrole
                  AND a.privilege_type = 'CONNECT')
            """);

        tablePrivileges.Should().BeEquivalentTo(new[]
        {
            "IversonReconciliationQueue:INSERT", "IversonReconciliationQueue:DELETE",
            "IversonTenants:SELECT", "IversonTenants:INSERT", "IversonTenants:UPDATE", "IversonTenants:DELETE",
            "iverson_token_revocations:SELECT", "iverson_token_revocations:INSERT", "iverson_token_revocations:UPDATE",
            "_iverson_schema:SELECT",
            "IversonPrivilegedRequests:INSERT", "IversonPrivilegedRequests:SELECT",
            "IversonStarRocksTenants:SELECT",
        });
        columnPrivileges.Should().BeEquivalentTo(new[]
        {
            "IversonReconciliationQueue.Id:SELECT",
            "IversonDlqMessages.Id:SELECT", "IversonDlqMessages.SourceTopic:SELECT",
            "IversonDlqMessages.ConsumerGroup:SELECT", "IversonDlqMessages.MessageKey:SELECT",
            "IversonDlqMessages.ExceptionType:SELECT", "IversonDlqMessages.ExceptionMessage:SELECT",
            "IversonDlqMessages.Attempts:SELECT", "IversonDlqMessages.FailedAt:SELECT",
            "IversonDlqMessages.Replayed:SELECT", "IversonDlqMessages.TenantId:SELECT",
            "IversonPrivilegedRequests.Status:UPDATE",
        });
        connect.Should().BeTrue("the Authentik hook revokes PUBLIC's CONNECT on Helm, so the worker grants it");
    }

    [Fact]
    public async Task DataPath_WritesReadsAndDeletes_WithTheOutboxInTheSameTransaction()
    {
        var table = "widgets_" + Guid.NewGuid().ToString("N")[..8];
        var schema = WidgetSchema(table);
        await fixture.OwnerSchemaManager.ApplySchemaAsync(schema);

        var writer = new OutboxWriter(ReconciliationSchema.TableName, _api, _api);
        var entities = new EntityRepository(_api);
        var key = Guid.NewGuid().ToString();
        var parentId = Guid.NewGuid().ToString();
        var json = $$"""{"Id":"{{key}}","Name":"w","ParentId":"{{parentId}}"}""";

        // Post and Update: the upsert under iverson_runtime, then the outbox insert on the login.
        var outboxRowId = await writer.UpsertAndEnqueueOutboxAsync(schema, "Widget", key, json, "tenant-a");

        // Get, GetMany and OneToMany hydration, each filtered to the caller's tenant by RLS.
        (await entities.FetchByKeyAsync(schema, key, EntityAccess.ForTenant("tenant-a")))
            .Should().Contain("\"Name\":\"w\"");
        (await entities.FetchByKeyAsync(schema, key, EntityAccess.ForTenant("tenant-b"))).Should().BeNull();
        (await entities.FetchManyByKeysAsync(schema, [key], EntityAccess.ForTenant("tenant-a"))).Should().ContainSingle();
        (await entities.FetchByColumnAsync(schema, "ParentId", parentId, EntityAccess.ForTenant("tenant-a")))
            .Should().ContainSingle();

        // The fast path deletes its own outbox row by Id.
        await writer.DeleteOutboxRowIfPresentAsync(outboxRowId);
        (await _owner.QuerySingleOrDefaultAsync<long>(
            $"""SELECT count(*) FROM "{ReconciliationSchema.TableName}" WHERE "Id" = @Id""", new { Id = outboxRowId }))
            .Should().Be(0);

        // Delete: the entity delete under iverson_runtime and the Deleted outbox row, in one transaction.
        var deleteRowId = Guid.CreateVersion7();
        await _api.ExecuteInTransactionAsync(async tx =>
        {
            await entities.DeleteAsync(tx, schema, key, EntityAccess.ForTenant("tenant-a"));
            await writer.EnqueueDeleteOutboxRowAsync(tx, deleteRowId, "Widget", key, json);
        });

        (await _owner.QuerySingleOrDefaultAsync<long>($"""SELECT count(*) FROM "{table}" """)).Should().Be(0);
        (await _owner.QuerySingleOrDefaultAsync<string>(
            $"""SELECT "EventType" FROM "{ReconciliationSchema.TableName}" WHERE "Id" = @Id""", new { Id = deleteRowId }))
            .Should().Be("Deleted");
    }

    [Fact]
    public async Task TenantLifecycle_EveryCallTheApiMakes_Succeeds()
    {
        var tenants = new TenantRepository(TenantSchema.TableName, _api);
        var id = "tenant_" + Guid.NewGuid().ToString("N")[..8];

        await tenants.InsertAsync(id, "Grants probe", "active");
        (await tenants.GetAsync(id))!.Status.Should().Be("active");
        (await tenants.ListAsync()).Should().Contain(t => t.Id == id);
        await tenants.UpdateStatusAsync(id, "suspended");
        (await tenants.UpdateStatusUnlessDeletedAsync(id, "active")).Should().BeTrue();

        // CreateTenant's rollback when the IdP call fails.
        await tenants.DeleteAsync(id);
        (await tenants.GetAsync(id)).Should().BeNull();
    }

    [Fact]
    public async Task TokenRevocation_RevokeTwiceAndList_Succeeds()
    {
        var revocations = new TokenRevocationRepository(_api);
        var sub = "sub-" + Guid.NewGuid().ToString("N");

        await revocations.RevokeAsync(sub);
        await revocations.RevokeAsync(sub); // the ON CONFLICT update arm

        (await revocations.ListAsync()).Should().Contain(r => r.Sub == sub);
    }

    [Fact]
    public async Task SchemaRegistry_LoadsButCannotBeWritten()
    {
        var registry = new SchemaRegistryRepository(_api);

        var load = () => registry.LoadAllAsync();
        await load.Should().NotThrowAsync();

        // Registration writes go through the worker (spec §5).
        await DeniedAsync(() => registry.UpsertAsync("Widget", "{}"));
    }

    [Fact]
    public async Task Dlq_TheListAndTheReplayCheckColumnsAreReadable_ButMessageValueIsNot()
    {
        var messageKey = "key-" + Guid.NewGuid().ToString("N");
        await new DlqRepository(DlqSchema.TableName, _owner).InsertAsync(new DlqMessage(
            "iverson.entity-events", "projection", messageKey, "{\"payload\":true}",
            "System.Exception", "boom", 3, DateTime.UtcNow, "tenant-a"));

        var dlq = new DlqRepository(DlqSchema.TableName, _api);
        var row = (await dlq.ListUnreplayedAsync(200)).Should().Contain(r => r.MessageKey == messageKey).Which;

        // The replay route's checks (spec §5.2): the api's own repository method, as the api login.
        var check = await dlq.GetUnreplayedSummaryAsync(row.Id);
        check.Should().NotBeNull();
        check!.TenantId.Should().Be("tenant-a");
        check.SourceTopic.Should().Be("iverson.entity-events");

        await DeniedAsync(() => _api.QueryAsync<string>($"""SELECT "MessageValue" FROM "{DlqSchema.TableName}" """));
        await DeniedAsync(() => _api.QueryAsync<dynamic>($"""SELECT * FROM "{DlqSchema.TableName}" """));
    }

    [Fact]
    public async Task PrivilegedRequests_InsertPollAndExpireSucceed_ButOnlyStatusIsWritable()
    {
        // The api's own repository methods, as the api login: insert, poll, expire.
        var requests = new PrivilegedRequestRepository(PrivilegedRequestsTable, _api);
        var id = Guid.NewGuid();
        await requests.InsertAsync(id, PrivilegedRequestKinds.ReconcileType, "{}", "operator", expiresAfter: null);

        (await requests.GetAsync(id))!.Status.Should().Be(PrivilegedRequestStatus.Pending);
        (await requests.ExpireIfPendingAsync(id)).Should().BeTrue();
        (await requests.GetAsync(id))!.Status.Should().Be(PrivilegedRequestStatus.Expired);

        await DeniedAsync(() => _api.ExecuteAsync(
            $"""UPDATE "{PrivilegedRequestsTable}" SET "ResultJson" = @Result::jsonb WHERE "Id" = @Id""",
            new { Id = id, Result = "{}" }));
    }

    [Fact]
    public async Task Outbox_PayloadsAreUnreadable()
    {
        await DeniedAsync(() => _api.QueryAsync<string>(
            $"""SELECT "Payload" FROM "{ReconciliationSchema.TableName}" """));
        await DeniedAsync(() => _api.QueryAsync<dynamic>(
            $"""SELECT * FROM "{ReconciliationSchema.TableName}" """));
    }

    [Fact]
    public async Task MaintenanceRole_CannotBeEntered_AndTheApisRoleCheckProbesOnlyTheRuntimeRole()
    {
        await DeniedAsync(() => _api.QueryAsync<int>("SELECT 1", null, RecordStoreRole.Maintenance));

        var asApi = new PostgresSchemaManager(fixture.ApiConnectionString, NullLogger<PostgresSchemaManager>.Instance);

        var runtimeProbe = () => asApi.EnsureCanEnterRuntimeRoleAsync();
        await runtimeProbe.Should().NotThrowAsync();

        // The worker's full check would refuse this login: it is no member of iverson_maintenance.
        var workerCheck = () => asApi.EnsureRolesAsync();
        (await workerCheck.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("GRANT iverson_maintenance TO iverson_api;");
    }

    [Fact]
    public async Task Ddl_IsDenied()
    {
        await DeniedAsync(() => _api.ExecuteAsync($"CREATE TABLE api_ddl_probe_{Guid.NewGuid():N} (id int)"));
        await DeniedAsync(() => _api.ExecuteAsync($"""ALTER TABLE "{TenantSchema.TableName}" ADD COLUMN "Probe" text"""));
    }

    [Fact]
    public async Task ReadinessProbe_IsFalseForALoginWithoutTheGrants_AndTrueForTheApiLogin()
    {
        var login = "nogrants_" + Guid.NewGuid().ToString("N")[..8];
        await _owner.ExecuteAsync($"CREATE ROLE \"{login}\" LOGIN PASSWORD 'probe-only'");

        try
        {
            var bare = new PostgresRepository(ConnectionStringFor(login, "probe-only"), NullLogger<PostgresRepository>.Instance);

            (await bare.QuerySingleOrDefaultAsync<bool>(ApiLoginGrants.ReadinessProbeSql)).Should().BeFalse();
            (await _api.QuerySingleOrDefaultAsync<bool>(ApiLoginGrants.ReadinessProbeSql)).Should().BeTrue();

            // The api's role check names the grant a login outside iverson_runtime is missing.
            var asBare = new PostgresSchemaManager(ConnectionStringFor(login, "probe-only"), NullLogger<PostgresSchemaManager>.Instance);
            var probe = () => asBare.EnsureCanEnterRuntimeRoleAsync();
            (await probe.Should().ThrowAsync<InvalidOperationException>())
                .Which.Message.Should().Contain($"GRANT iverson_runtime TO {login};");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await _owner.ExecuteAsync($"DROP ROLE \"{login}\"");
        }
    }

    [Fact]
    public async Task EnsureApiLogin_RunAgain_WithAndWithoutAPassword_IsIdempotent()
    {
        var withPassword = () => fixture.OwnerSchemaManager.EnsureApiLoginAsync(
            ApiLoginGrantsPostgresContainerFixture.ApiPassword, ApiLoginGrants.Statements, ApiLoginGrants.Tables, TimeSpan.Zero);
        var withoutPassword = () => fixture.OwnerSchemaManager.EnsureApiLoginAsync(
            null, ApiLoginGrants.Statements, ApiLoginGrants.Tables, TimeSpan.Zero);

        await withPassword.Should().NotThrowAsync();
        await withoutPassword.Should().NotThrowAsync();
        (await _api.QuerySingleOrDefaultAsync<bool>(ApiLoginGrants.ReadinessProbeSql)).Should().BeTrue();
    }

    [Fact]
    public async Task EnsureApiLogin_WaitsForTheSchemaApplyLockOfEveryTableItGrantsOn()
    {
        foreach (var table in ApiLoginGrants.Tables)
        {
            await using var holder = new NpgsqlConnection(fixture.OwnerConnectionString);
            await holder.OpenAsync();
            // ApplySchemaAsync's lock statement, verbatim: a replica applying this table's schema.
            await holder.ExecuteAsync("SELECT pg_advisory_lock(hashtext(@TableName)::bigint)", new { TableName = table });

            var ensure = fixture.RestoreApiLoginAsync();
            await Task.Delay(500);
            ensure.IsCompleted.Should().BeFalse($"the grants on {table} must wait for its schema apply");

            await holder.ExecuteAsync("SELECT pg_advisory_unlock(hashtext(@TableName)::bigint)", new { TableName = table });
            await ensure.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task EnsureApiLogin_WhenTheLoginIsMissingAndNoPasswordIsConfigured_ThrowsNamingTheLogin()
    {
        NpgsqlConnection.ClearAllPools();
        await _owner.ExecuteAsync("DROP OWNED BY iverson_api");
        await _owner.ExecuteAsync("DROP ROLE iverson_api");

        try
        {
            var act = () => fixture.OwnerSchemaManager.EnsureApiLoginAsync(null, ApiLoginGrants.Statements, ApiLoginGrants.Tables, TimeSpan.Zero);

            (await act.Should().ThrowAsync<InvalidOperationException>())
                .Which.Message.Should().Contain("iverson_api").And.Contain("Postgres__ApiLoginPassword");
        }
        finally
        {
            await fixture.RestoreApiLoginAsync();
        }
    }
}
