using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Iverson.Api.Schema;
using Iverson.Api.StarRocks;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Iverson.Sql;
using Iverson.StarRocks;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.StarRocks;

public sealed class StarRocksContainerFixture : IAsyncLifetime
{
    private const int MysqlPort = 9030;

    private readonly IContainer _container = new ContainerBuilder()
        .WithImage("starrocks/allin1-ubuntu:latest")
        .WithPortBinding(MysqlPort, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(MysqlPort))
        .Build();

    public string ConnectionString { get; private set; } = null!;
    public StarRocksRepository Repository { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        ConnectionString = new MySqlConnectionStringBuilder
        {
            Server                  = _container.Hostname,
            Port                    = (uint)_container.GetMappedPublicPort(MysqlPort),
            Database                = "iverson_test",
            UserID                  = "root",
            Password                = "",
            AllowPublicKeyRetrieval = true,
        }.ToString();

        // The MySQL port accepts TCP connections well before StarRocks's FE/BE bootstrap
        // finishes and the engine is actually ready to execute queries — a bare port-open
        // wait strategy is not sufficient. Retry a real query until it succeeds or we give up.
        await WaitUntilQueryReadyAsync(TimeSpan.FromMinutes(3));

        Repository = new StarRocksRepository(ConnectionString, NullLogger<StarRocksRepository>.Instance);
    }

    private async Task WaitUntilQueryReadyAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var dbName = new MySqlConnectionStringBuilder(ConnectionString).Database;
        var probeConnectionString = new MySqlConnectionStringBuilder(ConnectionString)
        {
            Database = ""
        }.ToString();

        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var conn = new MySqlConnection(probeConnectionString);
                await conn.OpenAsync();
                await using var cmd = new MySqlCommand("SELECT 1", conn);
                await cmd.ExecuteScalarAsync();

                // StarRocksRepository.ApplyTableAsync creates the target database lazily
                // (via its own private EnsureDatabaseAsync), but plain QueryAsync/ExecuteAsync
                // calls do not — they connect straight to `iverson_test`, which doesn't exist
                // yet on a fresh container. Create it here so every later test (whether or
                // not it goes through ApplyTableAsync first) can query against a real schema.
                await using var createCmd = new MySqlCommand($"CREATE DATABASE IF NOT EXISTS `{dbName}`", conn);
                await createCmd.ExecuteNonQueryAsync();

                // SELECT 1 (and DDL like CREATE DATABASE) only exercise the FE — StarRocks's
                // FE metadata service comes up and starts accepting the MySQL wire protocol well
                // before the BE (backend) process has finished starting and registered itself
                // with the FE. Any query that actually touches table data (CREATE TABLE, INSERT,
                // SELECT against a real table) fails with "Backend node not found" until the BE
                // is alive, which — observed directly against this image — can take noticeably
                // longer than FE readiness. Task 4's smoke test never hit this because it only
                // runs SELECT 1; Task 5 is the first set of tests to do real DDL/DML, so it's the
                // first to surface the gap. Gate readiness on BE aliveness too, not just FE.
                if (!await IsBackendAliveAsync(conn))
                {
                    lastError = new Exception("StarRocks backend was never reported alive (SHOW BACKENDS Alive=false)");
                    await Task.Delay(TimeSpan.FromSeconds(3));
                    continue;
                }

                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }

        throw new TimeoutException(
            $"StarRocks did not become query-ready within {timeout}.", lastError);
    }

    private static async Task<bool> IsBackendAliveAsync(MySqlConnection conn)
    {
        await using var cmd = new MySqlCommand("SHOW BACKENDS", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var aliveOrdinal = -1;
        while (await reader.ReadAsync())
        {
            if (aliveOrdinal < 0)
                aliveOrdinal = reader.GetOrdinal("Alive");

            if (string.Equals(reader.GetString(aliveOrdinal), "true", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

public sealed class StarRocksIntegrationTests(StarRocksContainerFixture fixture)
    : IClassFixture<StarRocksContainerFixture>
{
    private readonly StarRocksRepository _repo = fixture.Repository;

    // Use unique table names per test to avoid state leakage — the container and its
    // schema persist for the whole test class (IClassFixture), and StarRocks has no
    // per-test transactional rollback to lean on.
    private static string UniqueTable() =>
        "tbl_" + Guid.NewGuid().ToString("N")[..8];

    private static SchemaDescriptor AuthorSchema(string tableName) => new()
    {
        TypeName      = "Author",
        TableName     = tableName,
        KeyColumn     = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns =
        [
            new ColumnDescriptor("Name",        "text",        false),
            new ColumnDescriptor("Bio",         "text",        true),
            new ColumnDescriptor("Rating",      "integer",     true),
            new ColumnDescriptor("PublishedAt", "timestamptz", true),
        ],
        FkColumns    = [],
        VectorFields = [],
        ChunkFields  = [],
        Relations    = []
    };

    private static async Task CreateAndSeedAuthorsAsync(
        StarRocksRepository repo, string tableName, params (string Id, string Name, string? Bio, int? Rating, string? PublishedAt)[] rows)
    {
        var schema = new StarRocksTableSchema(
            tableName,
            new StarRocksColumnSchema("Id", "VARCHAR(36)", false),
            [
                new StarRocksColumnSchema("Name",        "STRING",   false),
                new StarRocksColumnSchema("Bio",         "STRING",   true),
                new StarRocksColumnSchema("Rating",      "INT",      true),
                new StarRocksColumnSchema("PublishedAt", "DATETIME", true),
            ]);

        await repo.ApplyTableAsync(schema);

        foreach (var row in rows)
        {
            var bio         = row.Bio is null ? "NULL" : $"'{row.Bio}'";
            var rating      = row.Rating is null ? "NULL" : row.Rating.ToString();
            var publishedAt = row.PublishedAt is null ? "NULL" : $"'{row.PublishedAt}'";
            await repo.ExecuteAsync(
                $"INSERT INTO `{tableName}` VALUES ('{row.Id}', '{row.Name}', {bio}, {rating}, {publishedAt})");
        }
    }

    private static SchemaRegistry BuildRegistry(params SchemaDescriptor[] schemas)
    {
        var sql = Substitute.For<IPostgresRepository>();
        sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);
        var registry = new SchemaRegistry(sql, NullLogger<SchemaRegistry>.Instance);
        foreach (var schema in schemas)
            registry.RegisterAsync(schema).GetAwaiter().GetResult();
        return registry;
    }

    [Fact]
    public async Task Fixture_ContainerStartsAndAcceptsQueries()
    {
        var result = await _repo.QueryAsync<int>("SELECT 1");
        result.Should().ContainSingle().Which.Should().Be(1);
    }

    [Fact]
    public async Task BuildSearch_EqualsClause_ExecutesAndReturnsMatchingRow()
    {
        var table = UniqueTable();
        await CreateAndSeedAuthorsAsync(_repo, table,
            ("11111111-1111-1111-1111-111111111111", "Alice", null, 4, null),
            ("22222222-2222-2222-2222-222222222222", "Bob",   null, 2, null));

        var query = new SearchQuery();
        query.Clauses.Add(new SearchClause
        {
            Property = "Name", Operator = SearchOperator.Equals,
            Value = new SearchValue { StringVal = "Alice" }, ClauseType = SearchClauseType.Filter
        });

        var (sql, param) = StarRocksQueryBuilder.BuildSearch(table, AuthorSchema(table), query, 0, 50);
        var rows = (await _repo.QueryAsync<dynamic>(sql, param)).ToList();

        rows.Should().ContainSingle();
        ((string)rows[0].Name).Should().Be("Alice");
    }

    [Fact]
    public async Task BuildSearch_StartsWithAndEndsWithClauses_ExecuteAndFilterCorrectly()
    {
        var table = UniqueTable();
        await CreateAndSeedAuthorsAsync(_repo, table,
            ("11111111-1111-1111-1111-111111111111", "Alexander", null, null, null),
            ("22222222-2222-2222-2222-222222222222", "Alice",     null, null, null),
            ("33333333-3333-3333-3333-333333333333", "Bob",       null, null, null));

        var startsWithQuery = new SearchQuery();
        startsWithQuery.Clauses.Add(new SearchClause
        {
            Property = "Name", Operator = SearchOperator.StartsWith,
            Value = new SearchValue { StringVal = "Al" }, ClauseType = SearchClauseType.Filter
        });
        var (startsSql, startsParam) = StarRocksQueryBuilder.BuildSearch(table, AuthorSchema(table), startsWithQuery, 0, 50);
        var startsRows = (await _repo.QueryAsync<dynamic>(startsSql, startsParam)).ToList();
        startsRows.Should().HaveCount(2);

        var endsWithQuery = new SearchQuery();
        endsWithQuery.Clauses.Add(new SearchClause
        {
            Property = "Name", Operator = SearchOperator.EndsWith,
            Value = new SearchValue { StringVal = "ce" }, ClauseType = SearchClauseType.Filter
        });
        var (endsSql, endsParam) = StarRocksQueryBuilder.BuildSearch(table, AuthorSchema(table), endsWithQuery, 0, 50);
        var endsRows = (await _repo.QueryAsync<dynamic>(endsSql, endsParam)).ToList();
        endsRows.Should().ContainSingle();
        ((string)endsRows[0].Name).Should().Be("Alice");
    }

    [Fact]
    public async Task BuildSearch_ComparisonOperators_ExecuteAndFilterByRating()
    {
        var table = UniqueTable();
        await CreateAndSeedAuthorsAsync(_repo, table,
            ("11111111-1111-1111-1111-111111111111", "Alice", null, 5, null),
            ("22222222-2222-2222-2222-222222222222", "Bob",   null, 3, null),
            ("33333333-3333-3333-3333-333333333333", "Carl",  null, 1, null));

        var query = new SearchQuery();
        query.Clauses.Add(new SearchClause
        {
            Property = "Rating", Operator = SearchOperator.GreaterThanOrEquals,
            Value = new SearchValue { NumberVal = 3 }, ClauseType = SearchClauseType.Filter
        });

        var (sql, param) = StarRocksQueryBuilder.BuildSearch(table, AuthorSchema(table), query, 0, 50);
        var rows = (await _repo.QueryAsync<dynamic>(sql, param)).ToList();

        rows.Should().HaveCount(2);
        ((IEnumerable<dynamic>)rows).Select(r => (string)r.Name).Should().BeEquivalentTo(["Alice", "Bob"]);
    }

    [Fact]
    public async Task BuildSearch_InClause_ExecutesAndReturnsAllMatches()
    {
        var table = UniqueTable();
        await CreateAndSeedAuthorsAsync(_repo, table,
            ("11111111-1111-1111-1111-111111111111", "Alice", null, null, null),
            ("22222222-2222-2222-2222-222222222222", "Bob",   null, null, null),
            ("33333333-3333-3333-3333-333333333333", "Carl",  null, null, null));

        var query = new SearchQuery();
        var strList = new RepeatedString();
        strList.Values.Add("Alice");
        strList.Values.Add("Carl");
        query.Clauses.Add(new SearchClause
        {
            Property = "Name", Operator = SearchOperator.In,
            Value = new SearchValue { StringList = strList }, ClauseType = SearchClauseType.Filter
        });

        var (sql, param) = StarRocksQueryBuilder.BuildSearch(table, AuthorSchema(table), query, 0, 50);
        var rows = (await _repo.QueryAsync<dynamic>(sql, param)).ToList();

        rows.Should().HaveCount(2);
        ((IEnumerable<dynamic>)rows).Select(r => (string)r.Name).Should().BeEquivalentTo(["Alice", "Carl"]);
    }

    [Fact]
    public async Task BuildSearch_WithInnerJoin_ExecutesAndReturnsOnlyMatchedRows()
    {
        var authorsTable  = UniqueTable();
        var articlesTable = UniqueTable();

        await CreateAndSeedAuthorsAsync(_repo, authorsTable,
            ("11111111-1111-1111-1111-111111111111", "Alice", null, null, null),
            ("22222222-2222-2222-2222-222222222222", "Bob",   null, null, null));

        var articleSchema = new StarRocksTableSchema(
            articlesTable,
            new StarRocksColumnSchema("Id", "VARCHAR(36)", false),
            [
                new StarRocksColumnSchema("AuthorId", "VARCHAR(36)", false),
                new StarRocksColumnSchema("Title",    "STRING",      false),
            ]);
        await _repo.ApplyTableAsync(articleSchema);
        await _repo.ExecuteAsync(
            $"INSERT INTO `{articlesTable}` VALUES " +
            $"('aaaaaaaa-1111-1111-1111-111111111111', '11111111-1111-1111-1111-111111111111', 'Alice''s First Post')");
        // Bob has no articles — an INNER JOIN must exclude him.

        var authorApiSchema = AuthorSchema(authorsTable);
        var articleApiSchema = new SchemaDescriptor
        {
            TypeName      = "Article",
            TableName     = articlesTable,
            KeyColumn     = new ColumnDescriptor("Id", "uuid", false),
            // AuthorId must be a ScalarColumn (not just an FkColumn) for BuildFromWithJoins'
            // ResolveColumn to find it — this mirrors real registered schemas, where
            // SchemaBuilder.BuildDescriptor adds every non-key property (including Id-suffixed
            // FK properties) to ScalarColumns as well as FkColumns (SchemaBuilder.cs:27-56).
            ScalarColumns = [new ColumnDescriptor("Title", "text", false), new ColumnDescriptor("AuthorId", "uuid", false)],
            FkColumns     = [new ForeignKeyDescriptor("AuthorId", "Author")],
            VectorFields  = [],
            ChunkFields   = [],
            Relations     = [new Iverson.Api.Schema.RelationDescriptor("Author", Iverson.Api.Schema.RelationKind.ManyToOne, "Author", "AuthorId")]
        };
        var registry = BuildRegistry(authorApiSchema, articleApiSchema);

        var joins = new List<JoinSpec>
        {
            new() { LeftType = "Author", RightType = "Article", LeftField = "Id", RightField = "AuthorId", Kind = JoinKind.Inner }
        };

        var (sql, param) = StarRocksQueryBuilder.BuildSearch(
            authorsTable, authorApiSchema, null, 0, 50, joins: joins, registry: registry);
        var rows = (await _repo.QueryAsync<dynamic>(sql, param)).ToList();

        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task BuildSearch_WithLeftJoin_ExecutesAndIncludesUnmatchedLeftRows()
    {
        var authorsTable  = UniqueTable();
        var articlesTable = UniqueTable();

        await CreateAndSeedAuthorsAsync(_repo, authorsTable,
            ("11111111-1111-1111-1111-111111111111", "Alice", null, null, null),
            ("22222222-2222-2222-2222-222222222222", "Bob",   null, null, null));

        var articleSchema = new StarRocksTableSchema(
            articlesTable,
            new StarRocksColumnSchema("Id", "VARCHAR(36)", false),
            [
                new StarRocksColumnSchema("AuthorId", "VARCHAR(36)", false),
                new StarRocksColumnSchema("Title",    "STRING",      false),
            ]);
        await _repo.ApplyTableAsync(articleSchema);
        await _repo.ExecuteAsync(
            $"INSERT INTO `{articlesTable}` VALUES " +
            $"('aaaaaaaa-1111-1111-1111-111111111111', '11111111-1111-1111-1111-111111111111', 'Alice''s First Post')");
        // Bob has no articles — a LEFT JOIN must still include him, unlike the INNER JOIN test above.

        var authorApiSchema = AuthorSchema(authorsTable);
        var articleApiSchema = new SchemaDescriptor
        {
            TypeName      = "Article",
            TableName     = articlesTable,
            KeyColumn     = new ColumnDescriptor("Id", "uuid", false),
            // AuthorId must be a ScalarColumn (not just an FkColumn) for BuildFromWithJoins'
            // ResolveColumn to find it — this mirrors real registered schemas, where
            // SchemaBuilder.BuildDescriptor adds every non-key property (including Id-suffixed
            // FK properties) to ScalarColumns as well as FkColumns (SchemaBuilder.cs:27-56).
            ScalarColumns = [new ColumnDescriptor("Title", "text", false), new ColumnDescriptor("AuthorId", "uuid", false)],
            FkColumns     = [new ForeignKeyDescriptor("AuthorId", "Author")],
            VectorFields  = [],
            ChunkFields   = [],
            Relations     = [new Iverson.Api.Schema.RelationDescriptor("Author", Iverson.Api.Schema.RelationKind.ManyToOne, "Author", "AuthorId")]
        };
        var registry = BuildRegistry(authorApiSchema, articleApiSchema);

        var joins = new List<JoinSpec>
        {
            new() { LeftType = "Author", RightType = "Article", LeftField = "Id", RightField = "AuthorId", Kind = JoinKind.Left }
        };

        var (sql, param) = StarRocksQueryBuilder.BuildSearch(
            authorsTable, authorApiSchema, null, 0, 50, joins: joins, registry: registry);
        var rows = (await _repo.QueryAsync<dynamic>(sql, param)).ToList();

        // Both Alice (matched) and Bob (unmatched) must appear — this is what actually
        // distinguishes a real LEFT JOIN from an INNER JOIN; a string-shape assertion
        // (as in StarRocksQueryBuilderTests.cs) cannot prove this, only live execution can.
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task BuildAggregate_Terms_ExecutesAndReturnsBucketCounts()
    {
        var table = UniqueTable();
        await CreateAndSeedAuthorsAsync(_repo, table,
            ("11111111-1111-1111-1111-111111111111", "Alice", null, null, null),
            ("22222222-2222-2222-2222-222222222222", "Alice", null, null, null),
            ("33333333-3333-3333-3333-333333333333", "Bob",   null, null, null));

        var spec = new AggregationDescriptor("by_name", AggregationKind.Terms, "Name", Size: 10);
        var (sql, param) = StarRocksQueryBuilder.BuildAggregate(table, AuthorSchema(table), null, spec);
        var rows = (await _repo.QueryAsync<dynamic>(sql, param)).ToList();

        rows.Should().HaveCount(2);
        var aliceBucket = rows.Single(r => (string)r.bucket_key == "Alice");
        ((long)aliceBucket.doc_count).Should().Be(2);
    }

    [Fact]
    public async Task BuildAggregate_Range_ExecutesAndReturnsCorrectBuckets()
    {
        var table = UniqueTable();
        await CreateAndSeedAuthorsAsync(_repo, table,
            ("11111111-1111-1111-1111-111111111111", "Alice", null, 2, null),
            ("22222222-2222-2222-2222-222222222222", "Bob",   null, 5, null),
            ("33333333-3333-3333-3333-333333333333", "Carl",  null, 9, null));

        var spec = new AggregationDescriptor(
            "rating_ranges", AggregationKind.Range, "Rating",
            RangeBuckets:
            [
                new RangeBucketDescriptor("low",  null, 3),
                new RangeBucketDescriptor("mid",  3,    7),
                new RangeBucketDescriptor("high", 7,    null),
            ]);
        var (sql, param) = StarRocksQueryBuilder.BuildAggregate(table, AuthorSchema(table), null, spec);
        var rows = (await _repo.QueryAsync<dynamic>(sql, param)).ToList();

        rows.Should().HaveCount(3);
        // Casting to (long) before .Should() is required here: rows.Single(...) and its
        // .doc_count member access are both `dynamic`, and the C# runtime binder does not
        // resolve extension methods (like FluentAssertions' .Should()) against a dynamic
        // receiver — it only looks for real instance members on the runtime type, so an
        // uncast dynamic.Should() throws RuntimeBinderException: "'long' does not contain a
        // definition for 'Should'". The Terms test above already worked around this with an
        // explicit (long) cast; the brief's Range/DateHistogram examples omitted it.
        ((long)rows.Single(r => (string)r.bucket_key == "low").doc_count).Should().Be(1L);
        ((long)rows.Single(r => (string)r.bucket_key == "mid").doc_count).Should().Be(1L);
        ((long)rows.Single(r => (string)r.bucket_key == "high").doc_count).Should().Be(1L);
    }

    [Fact]
    public async Task BuildAggregate_DateHistogram_Month_ExecutesAndGroupsCorrectly()
    {
        var table = UniqueTable();
        await CreateAndSeedAuthorsAsync(_repo, table,
            ("11111111-1111-1111-1111-111111111111", "Alice", null, null, "2026-01-15 00:00:00"),
            ("22222222-2222-2222-2222-222222222222", "Bob",   null, null, "2026-01-20 00:00:00"),
            ("33333333-3333-3333-3333-333333333333", "Carl",  null, null, "2026-02-01 00:00:00"));

        var spec = new AggregationDescriptor(
            "by_month", AggregationKind.DateHistogram, "PublishedAt",
            CalendarInterval: "month");
        var (sql, param) = StarRocksQueryBuilder.BuildAggregate(table, AuthorSchema(table), null, spec);
        var rows = (await _repo.QueryAsync<dynamic>(sql, param)).ToList();

        rows.Should().HaveCount(2);
        // See the (long) cast comment in BuildAggregate_Range_ExecutesAndReturnsCorrectBuckets
        // above — an uncast dynamic .doc_count.Should() throws RuntimeBinderException.
        ((long)rows.Single(r => (string)r.bucket_key == "2026-01").doc_count).Should().Be(2L);
        ((long)rows.Single(r => (string)r.bucket_key == "2026-02").doc_count).Should().Be(1L);
    }

    [Fact]
    public async Task BuildAggregate_DateHistogram_Quarter_ExecutesAndGroupsCorrectly()
    {
        var table = UniqueTable();
        await CreateAndSeedAuthorsAsync(_repo, table,
            ("11111111-1111-1111-1111-111111111111", "Alice", null, null, "2026-01-15 00:00:00"),
            ("22222222-2222-2222-2222-222222222222", "Bob",   null, null, "2026-02-20 00:00:00"),
            ("33333333-3333-3333-3333-333333333333", "Carl",  null, null, "2026-04-10 00:00:00"));

        var spec = new AggregationDescriptor(
            "by_quarter", AggregationKind.DateHistogram, "PublishedAt",
            CalendarInterval: "quarter");
        var (sql, param) = StarRocksQueryBuilder.BuildAggregate(table, AuthorSchema(table), null, spec);
        var rows = (await _repo.QueryAsync<dynamic>(sql, param)).ToList();

        rows.Should().HaveCount(2);
        // DateBucketExpr special-cases "quarter" as CONCAT(YEAR(col), '-Q', QUARTER(col)) —
        // StarRocks's DATE_FORMAT has no quarter directive, so unlike every other interval
        // (which formats via DATE_FORMAT) the bucket key here is "<year>-Q<quarter>", e.g.
        // "2026-Q1", not a DATE_FORMAT pattern. This is the single most dialect-risky
        // expression in the date-bucketing code, so it gets its own live-execution proof
        // (StarRocksQueryBuilderTests.cs only asserts the SQL string shape).
        // See the (long) cast comment in BuildAggregate_Range_ExecutesAndReturnsCorrectBuckets
        // above — an uncast dynamic .doc_count.Should() throws RuntimeBinderException.
        ((long)rows.Single(r => (string)r.bucket_key == "2026-Q1").doc_count).Should().Be(2L);
        ((long)rows.Single(r => (string)r.bucket_key == "2026-Q2").doc_count).Should().Be(1L);
    }

    [Fact]
    public async Task BuildAggregate_Having_ExecutesAndFiltersAggregatedResults()
    {
        var table = UniqueTable();
        await CreateAndSeedAuthorsAsync(_repo, table,
            ("11111111-1111-1111-1111-111111111111", "Alice", null, null, null),
            ("22222222-2222-2222-2222-222222222222", "Alice", null, null, null),
            ("33333333-3333-3333-3333-333333333333", "Bob",   null, null, null));

        var spec = new AggregationDescriptor("by_name", AggregationKind.Terms, "Name", Size: 10);
        var having = new SearchQuery();
        having.Clauses.Add(new SearchClause
        {
            Property = "doc_count", Operator = SearchOperator.GreaterThan,
            Value = new SearchValue { NumberVal = 1 }, ClauseType = SearchClauseType.Filter
        });

        var (sql, param) = StarRocksQueryBuilder.BuildAggregate(table, AuthorSchema(table), null, spec, having);
        var rows = (await _repo.QueryAsync<dynamic>(sql, param)).ToList();

        // Only "Alice" (count=2) clears the HAVING doc_count > 1 bar; "Bob" (count=1) doesn't.
        rows.Should().ContainSingle();
        ((string)rows[0].bucket_key).Should().Be("Alice");
    }

    [Fact]
    public async Task BuildGroupBy_CompoundMultiMetric_ExecutesAndReturnsAllMetrics()
    {
        var table = UniqueTable();
        await CreateAndSeedAuthorsAsync(_repo, table,
            ("11111111-1111-1111-1111-111111111111", "Alice", null, 3, null),
            ("22222222-2222-2222-2222-222222222222", "Alice", null, 5, null),
            ("33333333-3333-3333-3333-333333333333", "Bob",   null, 2, null));

        var request = new GroupByRequest
        {
            TypeName = "Author",
            Keys     = { "Name" },
            Limit    = 100,
        };
        request.Metrics.Add(new MetricSpec { Name = "sum_rating", Type = AggregationType.Sum, Field = "Rating" });
        request.Metrics.Add(new MetricSpec { Name = "cnt",        Type = AggregationType.Count });
        request.OrderBy.Add(new SearchSort { Property = "Name" });

        var registry = BuildRegistry(AuthorSchema(table));
        var (sql, param) = StarRocksQueryBuilder.BuildGroupBy(table, AuthorSchema(table), request, registry);
        var rows = (await _repo.QueryAsync<dynamic>(sql, param)).ToList();

        rows.Should().HaveCount(2);
        var alice = rows.Single(r => (string)r.Name == "Alice");
        ((long)alice.sum_rating).Should().Be(8);
        ((long)alice.cnt).Should().Be(2);
        var bob = rows.Single(r => (string)r.Name == "Bob");
        ((long)bob.sum_rating).Should().Be(2);
        ((long)bob.cnt).Should().Be(1);
    }

    [Fact]
    public async Task BuildGroupBy_WithJoin_WhereOnJoinedColumn_ExecutesAndActuallyFilters()
    {
        var authorsTable  = UniqueTable();
        var articlesTable = UniqueTable();

        await CreateAndSeedAuthorsAsync(_repo, authorsTable,
            ("11111111-1111-1111-1111-111111111111", "Alice", null, 3, null),
            ("22222222-2222-2222-2222-222222222222", "Bob",   null, 5, null));

        var articleSchema = new StarRocksTableSchema(
            articlesTable,
            new StarRocksColumnSchema("Id", "VARCHAR(36)", false),
            [
                new StarRocksColumnSchema("AuthorId", "VARCHAR(36)", false),
                new StarRocksColumnSchema("Title",    "STRING",      false),
            ]);
        await _repo.ApplyTableAsync(articleSchema);
        await _repo.ExecuteAsync(
            $"INSERT INTO `{articlesTable}` VALUES " +
            $"('aaaaaaaa-1111-1111-1111-111111111111', '11111111-1111-1111-1111-111111111111', 'Wanted Title'), " +
            $"('bbbbbbbb-2222-2222-2222-222222222222', '22222222-2222-2222-2222-222222222222', 'Other Title')");

        var authorApiSchema = AuthorSchema(authorsTable);
        var articleApiSchema = new SchemaDescriptor
        {
            TypeName      = "Article",
            TableName     = articlesTable,
            KeyColumn     = new ColumnDescriptor("Id", "uuid", false),
            // AuthorId must be a ScalarColumn (not just an FkColumn) for BuildGroupBy's join
            // resolution (ResolveColumn) to find it — this mirrors real registered schemas,
            // where SchemaBuilder.BuildDescriptor adds every non-key property (including
            // Id-suffixed FK properties) to ScalarColumns as well as FkColumns
            // (SchemaBuilder.cs:27-56). See the identical fix in the Task 5 join tests above.
            ScalarColumns = [new ColumnDescriptor("Title", "text", false), new ColumnDescriptor("AuthorId", "uuid", false)],
            FkColumns     = [new ForeignKeyDescriptor("AuthorId", "Author")],
            VectorFields  = [],
            ChunkFields   = [],
            Relations     = [new Iverson.Api.Schema.RelationDescriptor("Author", Iverson.Api.Schema.RelationKind.ManyToOne, "Author", "AuthorId")]
        };
        var registry = BuildRegistry(authorApiSchema, articleApiSchema);

        var request = new GroupByRequest
        {
            TypeName = "Author",
            Keys     = { "Name" },
            Limit    = 100,
            Query    = new SearchQuery(),
            Joins    = { new JoinSpec { LeftType = "Author", RightType = "Article", LeftField = "Id", RightField = "AuthorId", Kind = JoinKind.Inner } }
        };
        request.Query.Clauses.Add(new SearchClause
        {
            Property = "Article.Title", Operator = SearchOperator.Equals,
            Value = new SearchValue { StringVal = "Wanted Title" }, ClauseType = SearchClauseType.Filter
        });
        request.Metrics.Add(new MetricSpec { Name = "cnt", Type = AggregationType.Count });

        var (sql, param) = StarRocksQueryBuilder.BuildGroupBy(authorsTable, authorApiSchema, request, registry);
        var rows = (await _repo.QueryAsync<dynamic>(sql, param)).ToList();

        // Only Alice's row (whose article matches "Wanted Title") should survive the
        // join-aware WHERE filter — if the tableMap-to-BuildWhere wiring regressed, this
        // filter would silently vanish and both Alice and Bob would come back.
        rows.Should().ContainSingle();
        ((string)rows[0].Name).Should().Be("Alice");
    }
}
