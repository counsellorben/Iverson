using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Iverson.Client.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Xunit;

namespace Iverson.StarRocks.Tests;

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

                // Plain QueryAsync/ExecuteAsync calls connect straight to `iverson_test`, which
                // doesn't exist yet on a fresh container. Create it here so every later test can
                // query against a real schema.
                await using var createCmd = new MySqlCommand($"CREATE DATABASE IF NOT EXISTS `{dbName}`", conn);
                await createCmd.ExecuteNonQueryAsync();

                // SELECT 1 (and DDL like CREATE DATABASE) only exercise the FE — StarRocks's
                // FE metadata service comes up and starts accepting the MySQL wire protocol well
                // before the BE (backend) process has finished starting and registered itself
                // with the FE. Any query that actually touches table data (CREATE TABLE, INSERT,
                // SELECT against a real table) fails with "Backend node not found" until the BE
                // is alive, which — observed directly against this image — can take noticeably
                // longer than FE readiness. Gate readiness on BE aliveness too, not just FE.
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

    private static StarRocksQuerySchema AuthorSchema(string tableName) =>
        new("Author", tableName, "Id", ["Name", "Bio", "Rating", "PublishedAt"]);

    private async Task CreateAndSeedAuthorsAsync(
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

        await repo.ExecuteAsync(StarRocksSchemaManager.BuildCreateTableDdl(schema, $"`{tableName}`"));

        foreach (var row in rows)
        {
            var bio         = row.Bio is null ? "NULL" : $"'{row.Bio}'";
            var rating      = row.Rating is null ? "NULL" : row.Rating.ToString();
            var publishedAt = row.PublishedAt is null ? "NULL" : $"'{row.PublishedAt}'";
            await repo.ExecuteAsync(
                $"INSERT INTO `{tableName}` VALUES ('{row.Id}', '{row.Name}', {bio}, {rating}, {publishedAt})");
        }
    }

    [Fact]
    public async Task Fixture_ContainerStartsAndAcceptsQueries()
    {
        var result = await _repo.QueryAsync<int>("SELECT 1");
        result.Should().ContainSingle().Which.Should().Be(1);
    }

    [Fact]
    public async Task SearchAsync_EqualsClause_ExecutesAndReturnsMatchingRow()
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

        var rows = (await _repo.SearchAsync(AuthorSchema(table), query, 0, 50)).ToList();

        rows.Should().ContainSingle();
        ((string)rows[0].Name).Should().Be("Alice");
    }

    [Fact]
    public async Task SearchAsync_StartsWithAndEndsWithClauses_ExecuteAndFilterCorrectly()
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
        var startsRows = (await _repo.SearchAsync(AuthorSchema(table), startsWithQuery, 0, 50)).ToList();
        startsRows.Should().HaveCount(2);

        var endsWithQuery = new SearchQuery();
        endsWithQuery.Clauses.Add(new SearchClause
        {
            Property = "Name", Operator = SearchOperator.EndsWith,
            Value = new SearchValue { StringVal = "ce" }, ClauseType = SearchClauseType.Filter
        });
        var endsRows = (await _repo.SearchAsync(AuthorSchema(table), endsWithQuery, 0, 50)).ToList();
        endsRows.Should().ContainSingle();
        ((string)endsRows[0].Name).Should().Be("Alice");
    }

    [Fact]
    public async Task SearchAsync_ComparisonOperators_ExecuteAndFilterByRating()
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

        var rows = (await _repo.SearchAsync(AuthorSchema(table), query, 0, 50)).ToList();

        rows.Should().HaveCount(2);
        ((IEnumerable<dynamic>)rows).Select(r => (string)r.Name).Should().BeEquivalentTo(["Alice", "Bob"]);
    }

    [Fact]
    public async Task SearchAsync_InClause_ExecutesAndReturnsAllMatches()
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

        var rows = (await _repo.SearchAsync(AuthorSchema(table), query, 0, 50)).ToList();

        rows.Should().HaveCount(2);
        ((IEnumerable<dynamic>)rows).Select(r => (string)r.Name).Should().BeEquivalentTo(["Alice", "Carl"]);
    }

    [Fact]
    public async Task SearchAsync_WithInnerJoin_ExecutesAndReturnsOnlyMatchedRows()
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
        await _repo.ExecuteAsync(StarRocksSchemaManager.BuildCreateTableDdl(articleSchema, $"`{articlesTable}`"));
        await _repo.ExecuteAsync(
            $"INSERT INTO `{articlesTable}` VALUES " +
            $"('aaaaaaaa-1111-1111-1111-111111111111', '11111111-1111-1111-1111-111111111111', 'Alice''s First Post')");
        // Bob has no articles — an INNER JOIN must exclude him.

        var authorQuerySchema  = AuthorSchema(authorsTable);
        var articleQuerySchema = new StarRocksQuerySchema("Article", articlesTable, "Id", ["Title", "AuthorId"]);
        var registry = TestSchemaRegistry.BuildRegistry(authorQuerySchema, articleQuerySchema);

        var joins = new List<JoinSpec>
        {
            new() { LeftType = "Author", RightType = "Article", LeftField = "Id", RightField = "AuthorId", Kind = JoinKind.Inner }
        };

        var rows = (await _repo.SearchAsync(authorQuerySchema, null, 0, 50, joins: joins, registry: registry)).ToList();

        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task SearchAsync_WithLeftJoin_ExecutesAndIncludesUnmatchedLeftRows()
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
        await _repo.ExecuteAsync(StarRocksSchemaManager.BuildCreateTableDdl(articleSchema, $"`{articlesTable}`"));
        await _repo.ExecuteAsync(
            $"INSERT INTO `{articlesTable}` VALUES " +
            $"('aaaaaaaa-1111-1111-1111-111111111111', '11111111-1111-1111-1111-111111111111', 'Alice''s First Post')");
        // Bob has no articles — a LEFT JOIN must still include him, unlike the INNER JOIN test above.

        var authorQuerySchema  = AuthorSchema(authorsTable);
        var articleQuerySchema = new StarRocksQuerySchema("Article", articlesTable, "Id", ["Title", "AuthorId"]);
        var registry = TestSchemaRegistry.BuildRegistry(authorQuerySchema, articleQuerySchema);

        var joins = new List<JoinSpec>
        {
            new() { LeftType = "Author", RightType = "Article", LeftField = "Id", RightField = "AuthorId", Kind = JoinKind.Left }
        };

        var rows = (await _repo.SearchAsync(authorQuerySchema, null, 0, 50, joins: joins, registry: registry)).ToList();

        // Both Alice (matched) and Bob (unmatched) must appear — this is what actually
        // distinguishes a real LEFT JOIN from an INNER JOIN; a string-shape assertion
        // (as in StarRocksQueryBuilderTests.cs) cannot prove this, only live execution can.
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task AggregateAsync_Terms_ExecutesAndReturnsBucketCounts()
    {
        var table = UniqueTable();
        await CreateAndSeedAuthorsAsync(_repo, table,
            ("11111111-1111-1111-1111-111111111111", "Alice", null, null, null),
            ("22222222-2222-2222-2222-222222222222", "Alice", null, null, null),
            ("33333333-3333-3333-3333-333333333333", "Bob",   null, null, null));

        var spec = new AggregationDescriptor("by_name", AggregationKind.Terms, "Name", Size: 10);
        var result = await _repo.AggregateAsync(AuthorSchema(table), null, spec);

        // Exercises StarRocksRepository.AggregateAsync's bucketed-decode path end to end:
        // multiple raw {bucket_key, doc_count} rows must turn into multiple AggregationBucket
        // entries with the correct key and count each — not just "didn't throw".
        result.Should().NotBeNull();
        result!.Buckets.Should().HaveCount(2);
        result.Buckets!.Single(b => b.Key == "Alice").DocCount.Should().Be(2L);
        result.Buckets!.Single(b => b.Key == "Bob").DocCount.Should().Be(1L);
    }

    [Fact]
    public async Task AggregateAsync_Range_ExecutesAndReturnsCorrectBuckets()
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
        var result = await _repo.AggregateAsync(AuthorSchema(table), null, spec);

        result.Should().NotBeNull();
        result!.Buckets.Should().HaveCount(3);
        result.Buckets!.Single(b => b.Key == "low").DocCount.Should().Be(1L);
        result.Buckets!.Single(b => b.Key == "mid").DocCount.Should().Be(1L);
        result.Buckets!.Single(b => b.Key == "high").DocCount.Should().Be(1L);
    }

    [Fact]
    public async Task AggregateAsync_DateHistogram_Month_ExecutesAndGroupsCorrectly()
    {
        var table = UniqueTable();
        await CreateAndSeedAuthorsAsync(_repo, table,
            ("11111111-1111-1111-1111-111111111111", "Alice", null, null, "2026-01-15 00:00:00"),
            ("22222222-2222-2222-2222-222222222222", "Bob",   null, null, "2026-01-20 00:00:00"),
            ("33333333-3333-3333-3333-333333333333", "Carl",  null, null, "2026-02-01 00:00:00"));

        var spec = new AggregationDescriptor(
            "by_month", AggregationKind.DateHistogram, "PublishedAt",
            CalendarInterval: "month");
        var result = await _repo.AggregateAsync(AuthorSchema(table), null, spec);

        result.Should().NotBeNull();
        result!.Buckets.Should().HaveCount(2);
        result.Buckets!.Single(b => b.Key == "2026-01").DocCount.Should().Be(2L);
        result.Buckets!.Single(b => b.Key == "2026-02").DocCount.Should().Be(1L);
    }

    [Fact]
    public async Task AggregateAsync_DateHistogram_Quarter_ExecutesAndGroupsCorrectly()
    {
        var table = UniqueTable();
        await CreateAndSeedAuthorsAsync(_repo, table,
            ("11111111-1111-1111-1111-111111111111", "Alice", null, null, "2026-01-15 00:00:00"),
            ("22222222-2222-2222-2222-222222222222", "Bob",   null, null, "2026-02-20 00:00:00"),
            ("33333333-3333-3333-3333-333333333333", "Carl",  null, null, "2026-04-10 00:00:00"));

        var spec = new AggregationDescriptor(
            "by_quarter", AggregationKind.DateHistogram, "PublishedAt",
            CalendarInterval: "quarter");
        var result = await _repo.AggregateAsync(AuthorSchema(table), null, spec);

        // DateBucketExpr special-cases "quarter" as CONCAT(YEAR(col), '-Q', QUARTER(col)) —
        // StarRocks's DATE_FORMAT has no quarter directive, so unlike every other interval
        // (which formats via DATE_FORMAT) the bucket key here is "<year>-Q<quarter>", e.g.
        // "2026-Q1", not a DATE_FORMAT pattern. This is the single most dialect-risky
        // expression in the date-bucketing code, so it gets its own live-execution proof
        // (StarRocksQueryBuilderTests.cs only asserts the SQL string shape).
        result.Should().NotBeNull();
        result!.Buckets.Should().HaveCount(2);
        result.Buckets!.Single(b => b.Key == "2026-Q1").DocCount.Should().Be(2L);
        result.Buckets!.Single(b => b.Key == "2026-Q2").DocCount.Should().Be(1L);
    }

    [Fact]
    public async Task AggregateAsync_Having_ExecutesAndFiltersAggregatedResults()
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

        var result = await _repo.AggregateAsync(AuthorSchema(table), null, spec, having);

        // Only "Alice" (count=2) clears the HAVING doc_count > 1 bar; "Bob" (count=1) doesn't.
        result.Should().NotBeNull();
        result!.Buckets.Should().ContainSingle();
        result.Buckets![0].Key.Should().Be("Alice");
        result.Buckets![0].DocCount.Should().Be(2L);
    }

    [Fact]
    public async Task AggregateAsync_Sum_ExecutesAndReturnsMetricValue()
    {
        var table = UniqueTable();
        await CreateAndSeedAuthorsAsync(_repo, table,
            ("11111111-1111-1111-1111-111111111111", "Alice", null, 3, null),
            ("22222222-2222-2222-2222-222222222222", "Bob",   null, 5, null),
            ("33333333-3333-3333-3333-333333333333", "Carl",  null, 2, null));

        var spec = new AggregationDescriptor("total_rating", AggregationKind.Sum, "Rating");
        var result = await _repo.AggregateAsync(AuthorSchema(table), null, spec);

        // Metric-only aggregates (Sum/Avg/Min/Max/Count) take AggregateAsync's non-bucketed
        // decode path — Buckets stays null and the single scalar row is decoded into
        // MetricValue. Task 4's review flagged this branch as covered only by a
        // "throws on multi-key GroupByFields" guard, never by an assertion on the decoded
        // value; this proves the actual number comes back correct (3 + 5 + 2 = 10), not just
        // that AggregateAsync doesn't throw.
        result.Should().NotBeNull();
        result!.Buckets.Should().BeNull();
        result.MetricValue.Should().Be(10.0);
    }

    [Fact]
    public async Task GroupByAsync_CompoundMultiMetric_ExecutesAndReturnsAllMetrics()
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

        var registry = TestSchemaRegistry.BuildRegistry(AuthorSchema(table));
        var rows = (await _repo.GroupByAsync(AuthorSchema(table), request, registry)).ToList();

        rows.Should().HaveCount(2);
        var alice = rows.Single(r => (string)r.Name == "Alice");
        ((long)alice.sum_rating).Should().Be(8);
        ((long)alice.cnt).Should().Be(2);
        var bob = rows.Single(r => (string)r.Name == "Bob");
        ((long)bob.sum_rating).Should().Be(2);
        ((long)bob.cnt).Should().Be(1);
    }

    [Fact]
    public async Task GroupByAsync_WithJoin_WhereOnJoinedColumn_ExecutesAndActuallyFilters()
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
        await _repo.ExecuteAsync(StarRocksSchemaManager.BuildCreateTableDdl(articleSchema, $"`{articlesTable}`"));
        await _repo.ExecuteAsync(
            $"INSERT INTO `{articlesTable}` VALUES " +
            $"('aaaaaaaa-1111-1111-1111-111111111111', '11111111-1111-1111-1111-111111111111', 'Wanted Title'), " +
            $"('bbbbbbbb-2222-2222-2222-222222222222', '22222222-2222-2222-2222-222222222222', 'Other Title')");

        var authorQuerySchema  = AuthorSchema(authorsTable);
        var articleQuerySchema = new StarRocksQuerySchema("Article", articlesTable, "Id", ["Title", "AuthorId"]);
        var registry = TestSchemaRegistry.BuildRegistry(authorQuerySchema, articleQuerySchema);

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

        var rows = (await _repo.GroupByAsync(authorQuerySchema, request, registry)).ToList();

        // Only Alice's row (whose article matches "Wanted Title") should survive the
        // join-aware WHERE filter — if the tableMap-to-BuildWhere wiring regressed, this
        // filter would silently vanish and both Alice and Bob would come back.
        rows.Should().ContainSingle();
        ((string)rows[0].Name).Should().Be("Alice");
    }
}
