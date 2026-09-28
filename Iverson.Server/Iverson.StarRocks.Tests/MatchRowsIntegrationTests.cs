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
        // Same grants and connection-string shape as TenantIsolationIntegrationTests (see its comments).
        fx.Repository.ExecuteAsync("""
            CREATE USER IF NOT EXISTS 'iverson_app'@'%' IDENTIFIED BY 'test_pw';
            GRANT user_admin TO 'iverson_app'@'%';
            GRANT OPERATE ON SYSTEM TO 'iverson_app'@'%';
            GRANT CREATE DATABASE ON CATALOG default_catalog TO 'iverson_app'@'%';
            """).GetAwaiter().GetResult();
        _appConnectionString = new MySqlConnectionStringBuilder(fx.ConnectionString)
        {
            UserID = "iverson_app", Password = "test_pw", Database = ""
        }.ToString();
        _appRepo = new EngagementRepository(_appConnectionString, NullLogger<EngagementRepository>.Instance);
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
            await _fx.UnpauseAsync();
        }
        await dispose.WaitAsync(TimeSpan.FromSeconds(90));
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

    // CIR-1 Finding 2(a): StreamTenantScopedAsync's ReleaseAsync runs `SET ROLE NONE` before returning its
    // connection to the pool. `MaximumPoolSize=1` forces the very next connection opened on this same
    // connection string to be that exact physical connection; `ConnectionReset=false` stops MySqlConnector's
    // own pool-return reset from clearing session state itself, so only our own `SET ROLE NONE` (or its
    // absence, under the M4 mutant deleting it) decides what `CURRENT_ROLE()` reports next.
    // `SELECT CURRENT_ROLE()` was verified live against this StarRocks 4.1.1 image before writing this
    // assertion: it returns the bare role name string when a role is active, and the literal string
    // "NONE" (not SQL NULL) once `SET ROLE NONE` has run.
    [Fact]
    public async Task Stopping_a_read_early_still_clears_the_tenant_role_before_the_connection_is_reused()
    {
        var (tenant, schema) = await SeedAsync(Events);
        var pooledConnectionString = new MySqlConnectionStringBuilder(_appConnectionString)
        {
            MaximumPoolSize = 1, ConnectionReset = false
        }.ToString();
        var pooledRepo = new EngagementRepository(pooledConnectionString, NullLogger<EngagementRepository>.Instance);

        var reader = pooledRepo.MatchRowsAsync(schema, Req(oneRow: false), Authz(tenant)).GetAsyncEnumerator();
        (await reader.MoveNextAsync()).Should().BeTrue();   // one row read, then stop early
        await reader.DisposeAsync();

        await using var conn = new MySqlConnection(pooledConnectionString);
        await conn.OpenAsync();   // MaximumPoolSize=1: this is necessarily the same physical connection
        var role = await conn.ExecuteScalarAsync<string>("SELECT CURRENT_ROLE()");
        role.Should().Be("NONE");
    }

    // CIR-1 Finding 2(b): the open-phase catch in StreamTenantScopedAsync must release its connection even
    // when `SET ROLE` itself fails (an unprovisioned tenant), or that connection is never returned to the
    // pool. `MaximumPoolSize=1` makes a leak observable: under the M6 mutant (deleting the open-phase
    // `ReleaseAsync(c, null)` call), the pool's one slot stays held forever and the second read below never
    // gets a connection to open.
    [Fact]
    public async Task A_failed_SET_ROLE_releases_its_connection_so_the_next_read_can_still_use_the_pool()
    {
        var (tenant, schema) = await SeedAsync(Events);
        var pooledConnectionString = new MySqlConnectionStringBuilder(_appConnectionString)
        {
            MaximumPoolSize = 1
        }.ToString();
        var pooledRepo = new EngagementRepository(pooledConnectionString, NullLogger<EngagementRepository>.Instance);
        var neverProvisionedTenant = "m" + Guid.NewGuid().ToString("N")[..16];

        (await ReadAllAsync(pooledRepo.MatchRowsAsync(schema, Req(), Authz(neverProvisionedTenant))))
            .Should().BeEmpty();   // SET ROLE fails for a valid-but-never-provisioned tenant id

        var rows = await ReadAllAsync(pooledRepo.MatchRowsAsync(schema, Req(), Authz(tenant)))
            .WaitAsync(TimeSpan.FromSeconds(30));   // a leaked connection would hold the only pool slot
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
