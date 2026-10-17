using Dapper;
using FluentAssertions;
using Iverson.Client.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Xunit;

namespace Iverson.StarRocks.Tests;

/// <summary>
/// CSR round 12 §7: per-tenant StarRocks logins against a live engine. iverson_provisioner provisions each
/// tenant and creates its own login; reads and writes then log in as that tenant. iverson_app holds no tenant
/// role here (StarRocksSplitLogins), so every data statement that succeeds ran as a tenant login.
/// </summary>
[Trait("Category", "Integration")]
[Collection(StarRocksCollection.Name)]
public sealed class StarRocksTenantLoginIntegrationTests
{
    private readonly StarRocksContainerFixture _fx;
    private readonly string _appConnectionString;
    private readonly string _provisionerConnectionString;
    private readonly InMemoryProvisionedTenantDirectory _directory = new();
    private readonly EngagementRepository _repo;

    public StarRocksTenantLoginIntegrationTests(StarRocksContainerFixture fx)
    {
        _fx = fx;
        (_appConnectionString, _provisionerConnectionString) =
            StarRocksSplitLogins.EnsureAsync(fx).GetAwaiter().GetResult();
        _repo = Repository(StarRocksSplitLogins.TenantLoginKey, _directory);
    }

    private EngagementRepository Repository(string tenantLoginKey, IProvisionedTenantDirectory directory) =>
        new(_appConnectionString, NullLogger<EngagementRepository>.Instance,
            provisionerConnectionString: _provisionerConnectionString,
            tenantLoginKey: tenantLoginKey,
            provisionedTenants: directory);

    private static string UniqueTenantId() => "t" + Guid.NewGuid().ToString("N")[..16];
    private static string UniqueTable() => "tbl_" + Guid.NewGuid().ToString("N")[..8];

    private static EngagementTableSchema ProbeSchema(string table) =>
        new(table, new EngagementColumnSchema("Id", "VARCHAR(36)", false),
            [new EngagementColumnSchema("Name", "STRING", true)]);

    private static EngagementQuerySchema ProbeQuerySchema(string table) => new("Probe", table, "Id", ["Name"]);

    private static Dictionary<string, AuthorizationConstraint> AuthzFor(string tenantId) =>
        new() { ["Probe"] = new AuthorizationConstraint(null, null, null, TenantValue: tenantId) };

    private static string ProbePayload(string id, string name) =>
        System.Text.Json.JsonSerializer.Serialize(new { Id = id, Name = name });

    private static string Qualified(string tenant, string table) =>
        TenantIdentifier.Qualify(TenantIdentifier.DatabaseName(tenant), table);

    /// <summary>A fresh tenant with one table holding one row, provisioned and written through the repository.</summary>
    private async Task<(string Tenant, string Table)> ProvisionWithRowAsync(string name)
    {
        var tenant = UniqueTenantId();
        var table = UniqueTable();
        await _repo.EnsureTenantProvisionedAsync(tenant, ProbeSchema(table));
        await _repo.UpsertAsync(ProbeSchema(table), ProbePayload(Guid.NewGuid().ToString(), name), tenant);
        return (tenant, table);
    }

    private async Task<MySqlConnection> OpenAsTenantAsync(string tenant)
    {
        var conn = new MySqlConnection(
            EngagementRepository.TenantConnectionString(_appConnectionString, tenant, StarRocksSplitLogins.TenantLoginKey));
        await conn.OpenAsync();
        return conn;
    }

    // ── what a tenant login can do ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task TenantLogin_HasItsRoleActiveAtLogin_AndReadsItsOwnTable()
    {
        var (tenant, table) = await ProvisionWithRowAsync("Own");
        await using var conn = await OpenAsTenantAsync(tenant);

        (await conn.ExecuteScalarAsync<string>("SELECT CURRENT_ROLE()")).Should().Contain(TenantIdentifier.RoleName(tenant));
        (await conn.QueryAsync<string>($"SELECT Name FROM {Qualified(tenant, table)}")).Should().Equal("Own");
    }

    [Fact]
    public async Task TenantLogin_CarriesTheSessionCapsTheAppLoginCarries()
    {
        var (tenant, _) = await ProvisionWithRowAsync("Caps");
        await using var conn = await OpenAsTenantAsync(tenant);

        (await conn.ExecuteScalarAsync<long>("SELECT @@query_timeout")).Should().Be(180);
        (await conn.ExecuteScalarAsync<long>("SELECT @@exec_mem_limit")).Should().Be(4294967296);
    }

    // ── what it cannot do ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TenantLogin_IsDeniedAnotherTenantsTable()
    {
        var (tenantA, _) = await ProvisionWithRowAsync("A");
        var (tenantB, tableB) = await ProvisionWithRowAsync("B");
        await using var conn = await OpenAsTenantAsync(tenantA);

        var act = () => conn.QueryAsync<string>($"SELECT Name FROM {Qualified(tenantB, tableB)}");

        await act.Should().ThrowAsync<MySqlException>().WithMessage("*Access denied*");
    }

    [Fact]
    public async Task TenantLogin_CannotTakeOnAnotherTenantsRole()
    {
        var (tenantA, _) = await ProvisionWithRowAsync("A");
        var (tenantB, tableB) = await ProvisionWithRowAsync("B");
        await using var conn = await OpenAsTenantAsync(tenantA);

        var setRole = () => conn.ExecuteAsync($"SET ROLE `{TenantIdentifier.RoleName(tenantB)}`");
        await setRole.Should().ThrowAsync<MySqlException>();

        await conn.ExecuteAsync("SET ROLE ALL");
        var read = () => conn.QueryAsync<string>($"SELECT Name FROM {Qualified(tenantB, tableB)}");
        await read.Should().ThrowAsync<MySqlException>().WithMessage("*Access denied*");
    }

    [Fact]
    public async Task Provisioning_GrantsTheAppLoginNoTenantRole()
    {
        var (tenant, _) = await ProvisionWithRowAsync("NoGrant");

        var grants = await _fx.Repository.QueryAsync<dynamic>("SHOW GRANTS FOR 'iverson_app'@'%'");
        var text = string.Join("\n", grants
            .SelectMany(g => ((IDictionary<string, object>)g).Values)
            .Select(v => v?.ToString()));

        text.Should().NotContain(TenantIdentifier.RoleName(tenant));
    }

    // ── the provisioned-tenant record ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Provisioning_RecordsTheTenant()
    {
        var tenant = UniqueTenantId();
        (await _directory.IsProvisionedAsync(tenant)).Should().BeFalse();

        await _repo.EnsureTenantProvisionedAsync(tenant, ProbeSchema(UniqueTable()));

        (await _directory.IsProvisionedAsync(tenant)).Should().BeTrue();
    }

    [Fact]
    public async Task UnrecordedTenant_ReadsAsEmpty_EvenWhenItsDataExists()
    {
        var (tenant, table) = await ProvisionWithRowAsync("Hidden");
        var unrecorded = Repository(StarRocksSplitLogins.TenantLoginKey, new InMemoryProvisionedTenantDirectory());

        (await unrecorded.SearchAsync(ProbeQuerySchema(table), null, 0, 50, authz: AuthzFor(tenant))).Should().BeEmpty();
        (await unrecorded.AggregateAsync(ProbeQuerySchema(table), null,
            new AggregationDescriptor("cnt", AggregationKind.Count, "Name"), authz: AuthzFor(tenant))).Should().BeNull();
    }

    [Fact]
    public async Task RecordedTenant_WhoseLoginIsRefused_FailsAsNotReady()
    {
        // The gRPC read paths answer EngagementNotReadyException with Unavailable (ObjectSearchGrpcServiceTests).
        var (tenant, table) = await ProvisionWithRowAsync("WrongKey");
        var wrongKey = Repository("a-different-tenant-login-key", _directory);

        var act = () => wrongKey.SearchAsync(ProbeQuerySchema(table), null, 0, 50, authz: AuthzFor(tenant));

        (await act.Should().ThrowAsync<EngagementNotReadyException>())
            .Which.InnerException.Should().BeOfType<MySqlException>()
            .Which.ErrorCode.Should().Be(MySqlErrorCode.AccessDenied);
    }

    // ── the worker's writes and aggregates ──────────────────────────────────────────────────────

    [Fact]
    public async Task WorkerWritesAndAggregates_RunUnderTheTenantLogin()
    {
        var tenant = UniqueTenantId();
        var table = UniqueTable();
        await _repo.EnsureTenantProvisionedAsync(tenant, ProbeSchema(table));
        await _repo.UpsertAsync(ProbeSchema(table), ProbePayload("40000000-0000-0000-0000-000000000001", "One"), tenant);
        await _repo.UpsertAsync(ProbeSchema(table), ProbePayload("40000000-0000-0000-0000-000000000002", "Two"), tenant);
        await _repo.DeleteAsync(table, "Id", "40000000-0000-0000-0000-000000000001", tenant);

        var count = await _repo.AggregateAsync(ProbeQuerySchema(table), null,
            new AggregationDescriptor("cnt", AggregationKind.Count, "Name"), authz: AuthzFor(tenant));

        count.Should().NotBeNull();
        count!.MetricValue.Should().Be(1);
    }

    // ── one tenant's full pool (CSR round 12 §7.2) ──────────────────────────────────────────────

    // A full pool says nothing about StarRocks' health, so it must not count toward the process's one circuit
    // breaker, or one busy tenant would fail every tenant fast. Streams parked mid-read hold tenant A's whole
    // pool; A's next reads wait out the 2 s connect timeout and fail, and the same repository still serves B.
    [Fact]
    public async Task OneTenantsFullPool_FailsOnlyThatTenant_AndNeverOpensTheBreaker()
    {
        // The streams that fill tenant A's pool are opened through `holder`, built on the identical base string:
        // the same tenant connection string is the same MySqlConnector pool, so they fill the pool `repo` reads
        // from, while their ten successful opens land in holder's breaker, not repo's. repo's breaker then sees
        // only the reads below, and a predicate that counted a full pool would open it after two of them.
        var baseConnectionString = new MySqlConnectionStringBuilder(_appConnectionString) { ConnectionTimeout = 2 }.ToString();
        var holder = new EngagementRepository(
            baseConnectionString, NullLogger<EngagementRepository>.Instance,
            tenantLoginKey: StarRocksSplitLogins.TenantLoginKey,
            provisionedTenants: _directory);
        var repo = new EngagementRepository(
            baseConnectionString,
            NullLogger<EngagementRepository>.Instance,
            new EngagementResilienceOptions
            {
                CircuitBreaker = new EngagementCircuitBreakerOptions
                {
                    FailureRatio      = 0.5,
                    MinimumThroughput = 2,
                    SamplingDuration  = TimeSpan.FromMinutes(1),
                    BreakDuration     = TimeSpan.FromMinutes(1),
                },
            },
            provisionerConnectionString: _provisionerConnectionString,
            tenantLoginKey: StarRocksSplitLogins.TenantLoginKey,
            provisionedTenants: _directory);
        var (tenantA, tableA) = await ProvisionWithRowAsync("A");
        var (tenantB, tableB) = await ProvisionWithRowAsync("B");
        var oneRow = new MatchRowsRequest(
            [], SearchLogic.And, [], [new SearchSort { Property = "Name" }], [], [], true, [], 100);

        var held = new List<IAsyncEnumerator<IDictionary<string, object?>>>();
        try
        {
            for (var i = 0; i < EngagementRepository.TenantPoolSize; i++)
            {
                var stream = holder.MatchRowsAsync(ProbeQuerySchema(tableA), oneRow, AuthzFor(tenantA)).GetAsyncEnumerator();
                held.Add(stream);
                (await stream.MoveNextAsync()).Should().BeTrue();
            }

            for (var i = 0; i < 3; i++)
            {
                var act = () => repo.SearchAsync(ProbeQuerySchema(tableA), null, 0, 50, authz: AuthzFor(tenantA));
                await act.Should().ThrowAsync<MySqlException>().WithMessage("*All pooled connections are in use*");
            }

            (await repo.SearchAsync(ProbeQuerySchema(tableB), null, 0, 50, authz: AuthzFor(tenantB)))
                .Should().ContainSingle();
        }
        finally
        {
            foreach (var stream in held)
                await stream.DisposeAsync();
        }
    }
}
