using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Dapper;
using FluentAssertions;
using Iverson.Client.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Xunit;

namespace Iverson.StarRocks.Tests;

[Trait("Category", "Integration")]
[Collection(StarRocksCollection.Name)]
public sealed class MatchRowsIntegrationTests
{
    private readonly StarRocksContainerFixture _fx;
    private readonly EngagementRepository _rootRepo;
    private readonly EngagementRepository _appRepo;
    private readonly string _appConnectionString;

    public MatchRowsIntegrationTests(StarRocksContainerFixture fx)
    {
        _fx = fx;
        _rootRepo = fx.Repository;
        // The two production logins, set up by the same helper as TenantIsolationIntegrationTests (see
        // StarRocksSplitLogins). iverson_app is the base login only: every tenant query below runs on that
        // tenant's own login, derived from the tenant login key (CSR round 12 §7.2).
        (_appConnectionString, var provisionerConnectionString) =
            StarRocksSplitLogins.EnsureAsync(fx).GetAwaiter().GetResult();
        _appRepo = new EngagementRepository(_appConnectionString, NullLogger<EngagementRepository>.Instance,
            provisionerConnectionString: provisionerConnectionString,
            tenantLoginKey: StarRocksSplitLogins.TenantLoginKey,
            provisionedTenants: new InMemoryProvisionedTenantDirectory());
    }

    private static readonly (string Id, string UserId, string Kind, string At, double? Score, string OwnerId)[] Events =
    [
        ("1", "u1", "view", "2026-01-01 00:00:01", 1.0, "u1"),
        ("2", "u1", "save", "2026-01-01 00:00:02", 2.0, "u2"),
        ("3", "u2", "cite", "2026-01-01 00:00:03", 3.0, "u1"),
        ("4", "u2", "view", "2026-01-01 00:00:03", null, "u2"),   // ties with 3 on At
    ];

    private async Task<(string Tenant, EngagementQuerySchema Schema)> SeedAsync(
        params (string Id, string UserId, string Kind, string At, double? Score, string OwnerId)[] rows)
    {
        var tenant = "m" + Guid.NewGuid().ToString("N")[..16];
        var table = "ev_" + Guid.NewGuid().ToString("N")[..8];
        await _appRepo.EnsureTenantProvisionedAsync(tenant, new EngagementTableSchema(table,
            new EngagementColumnSchema("Id", "VARCHAR(36)", false),
            [
                new("UserId", "VARCHAR(36)", true), new("Kind", "STRING", true), new("At", "DATETIME", true),
                new("Score", "DOUBLE", true), new("OwnerId", "STRING", true), new("Blob", "VARBINARY", true),
                new("__TenantId", "STRING", true),
            ]));
        if (rows.Length > 0)
            await _rootRepo.ExecuteAsync(
                $"INSERT INTO `{TenantIdentifier.DatabaseName(tenant)}`.`{table}` " +
                "(`Id`, `UserId`, `Kind`, `At`, `Score`, `OwnerId`, `__TenantId`) VALUES " +
                string.Join(", ", rows.Select(r =>
                    $"('{r.Id}', '{r.UserId}', '{r.Kind}', '{r.At}', " +
                    $"{(r.Score is { } s ? s.ToString(CultureInfo.InvariantCulture) : "NULL")}, '{r.OwnerId}', '{tenant}')")));
        return (tenant, new EngagementQuerySchema("Event", table, "Id",
            ["UserId", "Kind", "At", "Score", "OwnerId", "Blob", "__TenantId"], TenantColumnName: "__TenantId"));
    }

    private static IReadOnlyDictionary<string, AuthorizationConstraint> Authz(string tenant, string? owner = null) =>
        new Dictionary<string, AuthorizationConstraint>(StringComparer.OrdinalIgnoreCase)
        {
            ["Event"] = new(null, owner is null ? null : "OwnerId", owner, "__TenantId", tenant)
        };

    private static MatchRowsRequest Req(
        string[]? partitionBy = null, (string Property, bool Descending)[]? orderBy = null, string[]? referenced = null,
        bool oneRow = true, SearchClause[]? where = null, int maxRowsScanned = 100_000) =>
        new(where ?? [], SearchLogic.And, partitionBy ?? [],
            (orderBy ?? [("At", false)]).Select(o => new SearchSort { Property = o.Property, Descending = o.Descending }).ToList(),
            referenced ?? [], [], oneRow, ["Blob"], maxRowsScanned);

    private static async Task<List<IDictionary<string, object?>>> ReadAllAsync(IAsyncEnumerable<IDictionary<string, object?>> rows)
    {
        var list = new List<IDictionary<string, object?>>();
        await foreach (var row in rows) list.Add(row);
        return list;
    }

    private static IEnumerable<object?> Ids(IEnumerable<IDictionary<string, object?>> rows) => rows.Select(r => r["Id"]);

    [Fact]
    public async Task Rows_arrive_by_partition_then_order_by_then_the_key_tie_breaker()
    {
        var (tenant, schema) = await SeedAsync(Events);

        var rows = await ReadAllAsync(_appRepo.MatchRowsAsync(schema,
            Req(partitionBy: ["UserId"], orderBy: [("At", true)]), Authz(tenant)));

        Ids(rows).Should().Equal("2", "1", "3", "4");   // u1 by At DESC; u2 tied on At, so by key
    }

    [Fact]
    public async Task One_row_projects_each_needed_column_once_under_its_canonical_name_and_lookups_ignore_case()
    {
        var (tenant, schema) = await SeedAsync(Events);

        var rows = await ReadAllAsync(_appRepo.MatchRowsAsync(schema,
            Req(partitionBy: ["userid"], referenced: ["score", "USERID"]), Authz(tenant)));

        rows[0].Keys.Should().Equal("Id", "UserId", "Score");
        rows[0]["USERID"].Should().Be("u1");
        rows[0]["score"].Should().Be(1.0);
    }

    [Fact]
    public async Task All_rows_projects_every_visible_column_but_bytes_and_tenant()
    {
        var (tenant, schema) = await SeedAsync(Events);

        var rows = await ReadAllAsync(_appRepo.MatchRowsAsync(schema, Req(oneRow: false), Authz(tenant)));

        rows[0].Keys.Should().Equal("Id", "UserId", "Kind", "At", "Score", "OwnerId");
    }

    [Fact]
    public async Task A_null_column_value_arrives_as_csharp_null_not_DBNull()
    {
        var (tenant, schema) = await SeedAsync(Events);

        var rows = await ReadAllAsync(_appRepo.MatchRowsAsync(schema, Req(referenced: ["Score"]), Authz(tenant)));

        rows.Single(r => (string)r["Id"]! == "4")["Score"].Should().BeNull();
    }

    [Fact]
    public async Task The_owner_row_filter_limits_the_rows_read()
    {
        var (tenant, schema) = await SeedAsync(Events);

        var rows = await ReadAllAsync(_appRepo.MatchRowsAsync(schema, Req(), Authz(tenant, owner: "u1")));

        Ids(rows).Should().Equal("1", "3");
    }

    private static SearchClause Clause(string property, SearchOperator op, SearchValue value,
        SearchClauseType type = SearchClauseType.Filter) =>
        new() { Property = property, Operator = op, Value = value, ClauseType = type };

    private static SearchValue Str(string s) => new() { StringVal = s };
    private static SearchValue Num(double d) => new() { NumberVal = d };

    public static TheoryData<string, string[]> Operators() => new()
    {
        { "EQUALS", ["1", "4"] }, { "NOT_EQUALS", ["2", "3"] }, { "CONTAINS", ["3"] }, { "STARTS_WITH", ["2"] },
        { "ENDS_WITH", ["1", "4"] }, { "GREATER_THAN", ["2", "3"] }, { "LESS_THAN", ["1", "2"] },
        { "GREATER_THAN_OR_EQUALS", ["2", "3"] }, { "LESS_THAN_OR_EQUALS", ["1", "2"] }, { "IN", ["2", "3"] },
        { "MUST_NOT", ["2", "3"] },
    };

    [Theory]
    [MemberData(nameof(Operators))]
    public async Task A_where_clause_of_each_operator_filters_the_rows_read(string op, string[] expectedIds)
    {
        var (tenant, schema) = await SeedAsync(Events);
        var clause = op switch
        {
            "EQUALS" => Clause("Kind", SearchOperator.Equals, Str("view")),
            "NOT_EQUALS" => Clause("Kind", SearchOperator.NotEquals, Str("view")),
            "CONTAINS" => Clause("Kind", SearchOperator.Contains, Str("it")),
            "STARTS_WITH" => Clause("Kind", SearchOperator.StartsWith, Str("sa")),
            "ENDS_WITH" => Clause("Kind", SearchOperator.EndsWith, Str("ew")),
            "GREATER_THAN" => Clause("Score", SearchOperator.GreaterThan, Num(1)),
            "LESS_THAN" => Clause("Score", SearchOperator.LessThan, Num(3)),
            "GREATER_THAN_OR_EQUALS" => Clause("Score", SearchOperator.GreaterThanOrEquals, Num(2)),
            "LESS_THAN_OR_EQUALS" => Clause("Score", SearchOperator.LessThanOrEquals, Num(2)),
            "IN" => Clause("Kind", SearchOperator.In, new SearchValue { StringList = new RepeatedString { Values = { "save", "cite" } } }),
            _ => Clause("Kind", SearchOperator.Equals, Str("view"), SearchClauseType.MustNot),
        };

        var rows = await ReadAllAsync(_appRepo.MatchRowsAsync(schema, Req(where: [clause]), Authz(tenant)));

        Ids(rows).Should().Equal(expectedIds);
    }

    [Fact]
    public async Task An_invalid_request_fails_on_enumeration_before_the_store_is_read()
    {
        var (tenant, schema) = await SeedAsync(Events);

        var act = () => ReadAllAsync(_appRepo.MatchRowsAsync(schema, Req(partitionBy: ["Blob"]), Authz(tenant)));

        await act.Should().ThrowAsync<EngagementQueryTranslationException>().WithMessage("*'Blob'*");
    }

    [Fact]
    public async Task One_row_past_MaxRowsScanned_is_read_so_the_caller_can_detect_overflow()
    {
        var (tenant, schema) = await SeedAsync(Events);

        var rows = await ReadAllAsync(_appRepo.MatchRowsAsync(schema, Req(maxRowsScanned: 2), Authz(tenant)));

        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task More_rows_than_one_batch_stream_through_in_order()
    {
        var (tenant, schema) = await SeedAsync();
        await InsertManyAsync(tenant, schema.TableName, 2_500, kindChars: 4);

        var count = 0;
        string? previous = null;
        await foreach (var row in _appRepo.MatchRowsAsync(schema, Req(orderBy: [("Id", false)]), Authz(tenant)))
        {
            var id = (string)row["Id"]!;
            if (previous is not null) string.CompareOrdinal(previous, id).Should().BeNegative();
            previous = id;
            count++;
        }
        count.Should().Be(2_500);
    }

    [Fact]
    public async Task A_null_or_invalid_tenant_or_a_missing_table_yields_an_empty_sequence()
    {
        var (tenant, schema) = await SeedAsync(Events);

        (await ReadAllAsync(_appRepo.MatchRowsAsync(schema, Req(), Authz(null!)))).Should().BeEmpty();
        (await ReadAllAsync(_appRepo.MatchRowsAsync(schema, Req(), Authz("bad tenant!")))).Should().BeEmpty();
        (await ReadAllAsync(_appRepo.MatchRowsAsync(schema with { TableName = "no_such_table" }, Req(), Authz(tenant))))
            .Should().BeEmpty();
        (await ReadAllAsync(_appRepo.MatchRowsAsync(schema, Req(), Authz("m" + Guid.NewGuid().ToString("N")[..16]))))
            .Should().BeEmpty();                                  // valid tenant id, never provisioned
    }

    [Fact]
    public async Task A_token_cancelled_while_the_query_is_starting_ends_the_read_early_and_the_pool_recovers()
    {
        var (tenant, schema) = await SeedAsync(Events);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var watch = Stopwatch.StartNew();

        var act = () => ReadAllAsync(_appRepo.StreamTenantScopedAsync(
            "sr.test", tenant, "SELECT SLEEP(6) AS s", new DynamicParameters(), _ => false, cts.Token));

        await act.Should().ThrowAsync<Exception>();          // a MySqlException (ParseError), per spec §3.2
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4), "the 6 s query must not run to completion");
        cts.IsCancellationRequested.Should().BeTrue();

        (await ReadAllAsync(_appRepo.MatchRowsAsync(schema, Req(), Authz(tenant)))).Should().HaveCount(4);
    }

    [Fact]
    public async Task Disposing_after_an_early_stop_waits_while_StarRocks_is_paused_and_completes_after_unpause()
    {
        // ~10 MB (20,000 rows x 500 chars) cannot sit in client buffers, so disposing the reader must drain
        // the rest from the server (spec Known issues; CDR-7 P98). This premise is why MatchPattern does not
        // await disposal after `limit` (Task 7).
        var (tenant, schema) = await SeedAsync();
        await InsertManyAsync(tenant, schema.TableName, 20_000, kindChars: 500);

        var reader = _appRepo.MatchRowsAsync(schema, Req(oneRow: false), Authz(tenant)).GetAsyncEnumerator();
        (await reader.MoveNextAsync()).Should().BeTrue();

        Task dispose = Task.CompletedTask;
        await _fx.PauseAsync();
        try
        {
            dispose = reader.DisposeAsync().AsTask();
            (await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(5)))).Should().NotBeSameAs(dispose);
        }
        finally
        {
            // A failed unpause surfaces as itself: the dispose wait is skipped, since it could only time out against a
            // paused container. Otherwise dispose is awaited (bounded) even when the assertion above failed, so the
            // reader's release never finishes unobserved on the shared container while the next test runs.
            await _fx.UnpauseAsync();
            await dispose.WaitAsync(TimeSpan.FromSeconds(90));
        }
    }

    [Fact]
    public async Task The_stream_activity_fails_on_an_open_or_read_failure_and_not_on_an_expected_missing_resource()
    {
        var (tenant, _) = await SeedAsync();
        var prefix = "sr.test-" + Guid.NewGuid().ToString("N")[..8] + ".";
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Iverson.StarRocks",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => { if (a.OperationName.StartsWith(prefix, StringComparison.Ordinal)) stopped.Enqueue(a); },
        };
        ActivitySource.AddActivityListener(listener);
        IAsyncEnumerable<IDictionary<string, object?>> Stream(string name, string sql, bool expected, CancellationToken ct = default) =>
            _appRepo.StreamTenantScopedAsync(prefix + name, tenant, sql, new DynamicParameters(), _ => expected, ct);

        var open = () => ReadAllAsync(Stream("open", "SELEC 1", expected: false));
        await open.Should().ThrowAsync<MySqlException>();
        (await ReadAllAsync(Stream("expected", "SELEC 1", expected: true))).Should().BeEmpty();
        using var cts = new CancellationTokenSource();
        var read = async () =>
        {
            await foreach (var _ in Stream("read", "SELECT 1 AS a UNION ALL SELECT 2 AS a", expected: false, cts.Token))
                cts.Cancel();                                   // the next read fails
        };
        await read.Should().ThrowAsync<OperationCanceledException>();
        (await ReadAllAsync(Stream("ok", "SELECT 1 AS a", expected: false))).Should().ContainSingle();

        var byName = stopped.ToDictionary(a => a.OperationName[prefix.Length..]);
        byName.Keys.Should().BeEquivalentTo(["open", "expected", "read", "ok"]);
        foreach (var failed in new[] { byName["open"], byName["read"] })
        {
            failed.Status.Should().Be(ActivityStatusCode.Error);
            failed.Events.Should().Contain(e => e.Name == "exception");
        }
        foreach (var fine in new[] { byName["expected"], byName["ok"] })
        {
            fine.Status.Should().Be(ActivityStatusCode.Ok);
            fine.Events.Should().BeEmpty();
        }
    }

    // CSR round 12 §7.2, replacing CIR-1 Finding 2(a)'s SET ROLE NONE test: tenant connection strings turn off
    // MySqlConnector's pooled reset, because StarRocks' reset would put the login's caps back to the global
    // defaults (spec Verified assumption 26). So whatever one read leaves on a connection is seen by the next
    // read from the same pool. Per-tenant logins give every tenant a pool of its
    // own, so that state stays inside the tenant. The first assertion checks the premise: this test can see
    // leftover state at all.
    [Fact]
    public async Task Session_state_a_read_leaves_behind_stays_inside_that_tenants_own_pool()
    {
        var (tenantA, _) = await SeedAsync(Events);
        var (tenantB, _) = await SeedAsync(Events);

        await ReadAllAsync(_appRepo.StreamTenantScopedAsync(
            "sr.test", tenantA, "SET query_timeout = 7", new DynamicParameters(), _ => false, default));

        (await QueryTimeoutAsync(tenantA)).Should().Be(7, "tenant pools do not reset session state");
        (await QueryTimeoutAsync(tenantB)).Should().Be(180, "tenant B's pool is its own, and its login carries the cap");

        async Task<long> QueryTimeoutAsync(string tenant)
        {
            var rows = await ReadAllAsync(_appRepo.StreamTenantScopedAsync(
                "sr.test", tenant, "SELECT @@query_timeout AS t", new DynamicParameters(), _ => false, default));
            return Convert.ToInt64(rows.Single()["t"]);
        }
    }

    // CIR-1 Finding 2(b), re-pointed to per-tenant pools (CSR round 12 §7.2): the open-phase catch in
    // StreamTenantScopedAsync must release its connection when the query fails to start, or that slot never
    // returns to the pool. A tenant's pool holds EngagementRepository.TenantPoolSize connections, so that many
    // failed starts would fill it under a leak, and the read after them could never open.
    [Fact]
    public async Task Failed_query_starts_release_their_connections_so_the_tenants_pool_never_fills()
    {
        var (tenant, schema) = await SeedAsync(Events);

        for (var i = 0; i < EngagementRepository.TenantPoolSize; i++)
            (await ReadAllAsync(_appRepo.StreamTenantScopedAsync(
                "sr.test", tenant, "SELEC 1", new DynamicParameters(), _ => true, default))).Should().BeEmpty();

        var rows = await ReadAllAsync(_appRepo.MatchRowsAsync(schema, Req(), Authz(tenant)))
            .WaitAsync(TimeSpan.FromSeconds(30));   // a leak would hold every slot: the open waits, then fails
        rows.Should().HaveCount(4);
    }

    private async Task InsertManyAsync(string tenant, string table, int count, int kindChars)
    {
        for (var start = 0; start < count; start += 1_000)
        {
            var values = Enumerable.Range(start, Math.Min(1_000, count - start)).Select(i =>
                $"('w{i:D6}', 'u1', REPEAT('x', {kindChars}), '2026-01-01 00:00:00', NULL, 'o', '{tenant}')");
            await _rootRepo.ExecuteAsync(
                $"INSERT INTO `{TenantIdentifier.DatabaseName(tenant)}`.`{table}` " +
                "(`Id`, `UserId`, `Kind`, `At`, `Score`, `OwnerId`, `__TenantId`) VALUES " + string.Join(", ", values));
        }
    }
}
