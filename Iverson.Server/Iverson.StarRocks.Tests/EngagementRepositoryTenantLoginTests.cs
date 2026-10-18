using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Iverson.Client.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Xunit;

namespace Iverson.StarRocks.Tests;

/// <summary>
/// CSR round 12 §7.2/§7.3: per-tenant StarRocks logins and the provisioned-tenant directory, without a server.
/// Port 1 refuses at once, so any call that reaches a connection waits out the readiness gate's 2 s and answers
/// <see cref="EngagementNotReadyException"/>; a call that answers anything else never connected.
/// </summary>
public class EngagementRepositoryTenantLoginTests
{
    private const string Unreachable = "Server=127.0.0.1;Port=1;User ID=iverson_app;Password=x;Connection Timeout=1";
    private const string Key = "test-tenant-login-key";

    private static readonly EngagementQuerySchema Schema = new("Probe", "probe", "Id", ["Name"]);

    private static readonly EngagementTableSchema TableSchema =
        new("probe", new EngagementColumnSchema("Id", "VARCHAR(36)", false),
            [new EngagementColumnSchema("Name", "STRING", true)]);

    private static EngagementRepository Repo(IProvisionedTenantDirectory? directory, string? key = Key) =>
        new(Unreachable, NullLogger<EngagementRepository>.Instance,
            new EngagementResilienceOptions { BackendReadyTimeout = TimeSpan.FromSeconds(2) },
            provisionerConnectionString: Unreachable,
            tenantLoginKey: key,
            provisionedTenants: directory);

    private static Dictionary<string, AuthorizationConstraint> AuthzFor(string tenantId) =>
        new() { ["Probe"] = new AuthorizationConstraint(null, null, null, TenantValue: tenantId) };

    // MySqlException's constructors are internal to MySqlConnector with no InternalsVisibleTo grant to this
    // assembly; built via reflection against the exact overload (StarRocksResiliencePipelineFactoryTests).
    private static MySqlException CreateMySqlException(MySqlErrorCode errorCode, string message) =>
        (MySqlException)typeof(MySqlException)
            .GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null,
                types: [typeof(MySqlErrorCode), typeof(string), typeof(string), typeof(Exception)],
                modifiers: null)!
            .Invoke([errorCode, null, message, null]);

    // ── the tenant's connection string ──────────────────────────────────────────────────────────

    [Fact]
    public void TenantConnectionString_LogsInAsTheTenantsOwnUser_WithItsOwnPoolBounds()
    {
        const string baseString =
            "Server=sr-fe;Port=9030;User ID=iverson_app;Password=app-pw;Database=iverson;SslMode=VerifyFull;SslCa=/etc/starrocks-tls/ca.crt";

        var built = new MySqlConnectionStringBuilder(EngagementRepository.TenantConnectionString(baseString, "acme", Key));

        built.UserID.Should().Be(TenantIdentifier.LoginName("acme"));
        built.Password.Should().Be(TenantIdentifier.LoginPassword("acme", Key));
        built.Database.Should().BeEmpty();
        built.MaximumPoolSize.Should().Be(10u);
        built.ConnectionIdleTimeout.Should().Be(60u);
        built.ConnectionReset.Should().BeFalse();
        built.Server.Should().Be("sr-fe");
        built.Port.Should().Be(9030u);
        built.SslMode.Should().Be(MySqlSslMode.VerifyFull);
        built.SslCa.Should().Be("/etc/starrocks-tls/ca.crt");
    }

    [Fact]
    public void TenantConnectionString_IsIdenticalForOneTenant_AndDistinctAcrossTenants()
    {
        // MySqlConnector keys its pools by connection string: one string per tenant is one pool per tenant.
        EngagementRepository.TenantConnectionString(Unreachable, "acme", Key)
            .Should().Be(EngagementRepository.TenantConnectionString(Unreachable, "acme", Key));
        EngagementRepository.TenantConnectionString(Unreachable, "acme", Key)
            .Should().NotBe(EngagementRepository.TenantConnectionString(Unreachable, "acme-2", Key));
    }

    // ── opening it ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AccessDenied_OnOpeningATenantConnection_SurfacesAsNotReady()
    {
        var original = CreateMySqlException(MySqlErrorCode.AccessDenied,
            "Access denied for user 'iverson_t_0123456789abcdefghijklmno'@'10.0.0.1' (using password: YES)");

        var act = () => EngagementRepository.OpenTenantConnectionAsync(() => Task.FromException(original));

        (await act.Should().ThrowAsync<EngagementNotReadyException>())
            .Which.InnerException.Should().BeSameAs(original);
    }

    [Fact]
    public async Task OtherFailure_OnOpeningATenantConnection_IsRethrownUnchanged()
    {
        var original = CreateMySqlException(MySqlErrorCode.UnableToConnectToHost,
            "Unable to connect to any of the specified MySQL hosts.");

        var act = () => EngagementRepository.OpenTenantConnectionAsync(() => Task.FromException(original));

        (await act.Should().ThrowAsync<MySqlException>()).Which.Should().BeSameAs(original);
    }

    // ── the provisioned-tenant directory ────────────────────────────────────────────────────────

    [Fact]
    public async Task UnrecordedTenant_EveryReaderAnswersEmpty_WithoutConnecting()
    {
        var repo = Repo(new InMemoryProvisionedTenantDirectory());
        var registry = TestSchemaRegistry.BuildRegistry(Schema);
        var groupBy = new GroupByRequest { TypeName = "Probe", Keys = { "Name" } };
        groupBy.Metrics.Add(new MetricSpec { Name = "cnt", Type = AggregationType.Count });
        var matchRows = new MatchRowsRequest(
            [], SearchLogic.And, [], [new SearchSort { Property = "Name" }], [], [], true, [], 100);

        (await repo.SearchAsync(Schema, null, 0, 50, authz: AuthzFor("t1"))).Should().BeEmpty();
        (await repo.AggregateAsync(Schema, null, new AggregationDescriptor("cnt", AggregationKind.Count, "Name"),
            authz: AuthzFor("t1"))).Should().BeNull();
        (await repo.GroupByAsync(Schema, groupBy, registry, AuthzFor("t1"))).Should().BeEmpty();
        (await repo.PipelineAsync(Schema, new PipelineRequest { TypeName = "Probe" }, registry, AuthzFor("t1")))
            .Should().BeEmpty();
        var rows = new List<IDictionary<string, object?>>();
        await foreach (var row in repo.MatchRowsAsync(Schema, matchRows, AuthzFor("t1")))
            rows.Add(row);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task UnrecordedTenant_DeleteHasNothingToRemove_AndDoesNotConnect()
    {
        var unrecorded = () => Repo(new InMemoryProvisionedTenantDirectory()).DeleteAsync("probe", "Id", "k1", "t1");

        await unrecorded.Should().NotThrowAsync();

        // The same delete for a recorded tenant connects, and port 1 makes that fail loudly, so the quiet
        // return above is one that never reached a connection.
        var directory = new InMemoryProvisionedTenantDirectory();
        await directory.RecordProvisionedAsync("t1");
        var recorded = () => Repo(directory).DeleteAsync("probe", "Id", "k1", "t1");

        await recorded.Should().ThrowAsync<EngagementNotReadyException>();
    }

    [Fact]
    public async Task RecordedTenant_IsConnectedFor()
    {
        var directory = new InMemoryProvisionedTenantDirectory();
        await directory.RecordProvisionedAsync("t1");

        var act = () => Repo(directory).SearchAsync(Schema, null, 0, 50, authz: AuthzFor("t1"));

        await act.Should().ThrowAsync<EngagementNotReadyException>();
    }

    [Fact]
    public async Task NoTenantLoginKey_RefusesTenantScopedReadsAndWrites_BeforeConnecting()
    {
        var directory = new InMemoryProvisionedTenantDirectory();
        await directory.RecordProvisionedAsync("t1");
        var repo = Repo(directory, key: null);

        var read = () => repo.SearchAsync(Schema, null, 0, 50, authz: AuthzFor("t1"));
        var write = () => repo.UpsertAsync(TableSchema, JsonSerializer.Serialize(new { Id = "k1", Name = "n" }), "t1");

        await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("*tenant login key*");
        await write.Should().ThrowAsync<InvalidOperationException>().WithMessage("*tenant login key*");
    }

    [Fact]
    public async Task NoDirectory_RefusesReadsAndDeletes_BeforeConnecting()
    {
        var repo = Repo(directory: null);
        var matchRows = new MatchRowsRequest(
            [], SearchLogic.And, [], [new SearchSort { Property = "Name" }], [], [], true, [], 100);

        var search = () => repo.SearchAsync(Schema, null, 0, 50, authz: AuthzFor("t1"));
        var match = async () =>
        {
            await foreach (var _ in repo.MatchRowsAsync(Schema, matchRows, AuthzFor("t1"))) { }
        };
        var delete = () => repo.DeleteAsync("probe", "Id", "k1", "t1");

        await search.Should().ThrowAsync<InvalidOperationException>().WithMessage("*provisioned-tenant directory*");
        await match.Should().ThrowAsync<InvalidOperationException>().WithMessage("*provisioned-tenant directory*");
        await delete.Should().ThrowAsync<InvalidOperationException>().WithMessage("*provisioned-tenant directory*");
    }

    // ── provisioning ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProvisioningThatFails_DoesNotRecordTheTenant()
    {
        var directory = new InMemoryProvisionedTenantDirectory();

        var act = () => Repo(directory).EnsureTenantProvisionedAsync("t1", TableSchema);

        await act.Should().ThrowAsync<EngagementNotReadyException>();
        (await directory.IsProvisionedAsync("t1")).Should().BeFalse();
    }

    [Fact]
    public async Task ProvisioningWithoutADirectory_RefusesBeforeConnecting()
    {
        var act = () => Repo(directory: null).EnsureTenantProvisionedAsync("t1", TableSchema);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*provisioned-tenant directory*");
    }
}
