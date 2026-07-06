using Dapper;
using FluentAssertions;
using Grpc.Core;
using Iverson.Api.Schema;
using Iverson.Api.StarRocks;
using Iverson.Api.Tests.Helpers;
using Iverson.Client.Contracts;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

using SrAggKind  = Iverson.StarRocks.AggregationKind;
using SrAggSpec  = Iverson.StarRocks.AggregationDescriptor;
using SrRangeSpec = Iverson.StarRocks.RangeBucketDescriptor;

namespace Iverson.Api.Tests.StarRocks;

public class StarRocksQueryBuilderTests
{
    // ── Fixtures ───────────────────────────────────────────────────────────────

    private static SchemaDescriptor AuthorSchema() => new()
    {
        TypeName      = "Author",
        TableName     = "authors",
        KeyColumn     = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns =
        [
            new ColumnDescriptor("Name",      "text",        false),
            new ColumnDescriptor("Bio",        "text",        true),
            new ColumnDescriptor("Rating",     "integer",     true),
            new ColumnDescriptor("PublishedAt","timestamptz", true),
        ],
        FkColumns    = [],
        VectorFields = [],
        ChunkFields  = [],
        Relations    = []
    };

    // ── BuildAggregate — Terms ─────────────────────────────────────────────────

    [Fact]
    public void BuildAggregate_Terms_ProducesGroupByWithLimit()
    {
        var spec = new SrAggSpec("by_name", SrAggKind.Terms, "Name", Size: 5);

        var (sql, _) = StarRocksQueryBuilder.BuildAggregate("authors", AuthorSchema(), null, spec);

        sql.Should().Contain("GROUP BY `Name`");
        sql.Should().Contain("ORDER BY doc_count DESC");
        sql.Should().Contain("LIMIT 5");
        sql.Should().Contain("bucket_key");
        sql.Should().Contain("doc_count");
    }

    // ── BuildAggregate — Range ─────────────────────────────────────────────────

    [Fact]
    public void BuildAggregate_Range_ProducesCaseExprWithEscapedKey()
    {
        var spec = new SrAggSpec(
            "rating_ranges", SrAggKind.Range, "Rating",
            RangeBuckets:
            [
                new SrRangeSpec("low",  null, 3),
                new SrRangeSpec("mid",  3,    7),
                new SrRangeSpec("high", 7,    null),
            ]);

        var (sql, _) = StarRocksQueryBuilder.BuildAggregate("authors", AuthorSchema(), null, spec);

        sql.Should().Contain("CASE");
        sql.Should().Contain("WHEN `Rating` < 3 THEN 'low'");
        sql.Should().Contain("WHEN `Rating` >= 3 AND `Rating` < 7 THEN 'mid'");
        sql.Should().Contain("WHEN `Rating` >= 7 THEN 'high'");
        sql.Should().Contain("bucket_key");
        sql.Should().Contain("doc_count");
    }

    [Fact]
    public void BuildAggregate_Range_EscapesSingleQuotesInKey()
    {
        var spec = new SrAggSpec(
            "r", SrAggKind.Range, "Rating",
            RangeBuckets: [new SrRangeSpec("it's high", 7, null)]);

        var (sql, _) = StarRocksQueryBuilder.BuildAggregate("authors", AuthorSchema(), null, spec);

        sql.Should().Contain("it''s high");
    }

    [Fact]
    public void BuildAggregate_Range_NoBuckets_AppendsHavingClause()
    {
        var spec = new SrAggSpec("rating_ranges", SrAggKind.Range, "Rating", RangeBuckets: null);

        var having = new SearchQuery();
        having.Clauses.Add(new SearchClause
        {
            Property   = "doc_count",
            Operator   = SearchOperator.GreaterThan,
            Value      = new SearchValue { NumberVal = 10 },
            ClauseType = SearchClauseType.Filter
        });

        var (sql, param) = StarRocksQueryBuilder.BuildAggregate("authors", AuthorSchema(), null, spec, having);

        sql.Should().Contain("SELECT NULL AS bucket_key, COUNT(*) AS doc_count FROM `authors`");
        sql.Should().Contain("HAVING `doc_count` > @h0");

        var lookup = (SqlMapper.IParameterLookup)param;
        lookup["h0"].Should().Be(10.0);
    }

    // ── BuildAggregate — multi-key GROUP BY ────────────────────────────────────

    [Fact]
    public void BuildAggregate_MultiKeyGroupBy_ProducesMultiColumnGroupBy()
    {
        var spec = new SrAggSpec(
            "by_name_rating", SrAggKind.Terms, "Name",
            GroupByFields: ["Name", "Rating"]);

        var (sql, _) = StarRocksQueryBuilder.BuildAggregate("authors", AuthorSchema(), null, spec);

        sql.Should().Contain("SELECT `Name`, `Rating`, COUNT(*) AS doc_count");
        sql.Should().Contain("GROUP BY `Name`, `Rating`");
        sql.Should().Contain("doc_count");
    }

    // ── BuildAggregate — raw expression metric ─────────────────────────────────

    [Fact]
    public void BuildAggregate_ExpressionMetric_UsesRawExpressionInAggFn()
    {
        var spec = new SrAggSpec(
            "revenue", SrAggKind.Sum, "Rating",
            Expression: "Rating * 2");

        var (sql, _) = StarRocksQueryBuilder.BuildAggregate("authors", AuthorSchema(), null, spec);

        sql.Should().Contain("SUM(Rating * 2)");
        sql.Should().NotContain("SUM(`Rating`)");
    }

    // ── BuildAggregate — HAVING ─────────────────────────────────────────────────

    [Fact]
    public void BuildAggregate_Having_AppendsHavingClause()
    {
        var spec = new SrAggSpec("by_name", SrAggKind.Terms, "Name", Size: 5);

        var having = new SearchQuery();
        having.Clauses.Add(new SearchClause
        {
            Property   = "doc_count",
            Operator   = SearchOperator.GreaterThan,
            Value      = new SearchValue { NumberVal = 10 },
            ClauseType = SearchClauseType.Filter
        });

        var (sql, param) = StarRocksQueryBuilder.BuildAggregate("authors", AuthorSchema(), null, spec, having);

        sql.Should().Contain("HAVING `doc_count` > @h0");
        // HAVING must land between GROUP BY and ORDER BY/LIMIT, not after them.
        sql.IndexOf("GROUP BY", StringComparison.Ordinal)
            .Should().BeLessThan(sql.IndexOf("HAVING", StringComparison.Ordinal));
        sql.IndexOf("HAVING", StringComparison.Ordinal)
            .Should().BeLessThan(sql.IndexOf("ORDER BY", StringComparison.Ordinal));

        var lookup = (SqlMapper.IParameterLookup)param;
        lookup["h0"].Should().Be(10.0);
    }

    [Fact]
    public void BuildAggregate_WhereAndHaving_UseDistinctParameterPrefixes()
    {
        var query = new SearchQuery();
        query.Clauses.Add(new SearchClause
        {
            Property   = "Name",
            Operator   = SearchOperator.Equals,
            Value      = new SearchValue { StringVal = "Alice" },
            ClauseType = SearchClauseType.Filter
        });

        var spec = new SrAggSpec("by_name", SrAggKind.Terms, "Name", Size: 5);

        var having = new SearchQuery();
        having.Clauses.Add(new SearchClause
        {
            Property   = "doc_count",
            Operator   = SearchOperator.GreaterThan,
            Value      = new SearchValue { NumberVal = 10 },
            ClauseType = SearchClauseType.Filter
        });

        var (sql, param) = StarRocksQueryBuilder.BuildAggregate("authors", AuthorSchema(), query, spec, having);

        sql.Should().Contain("WHERE `Name` = @p0");
        sql.Should().Contain("HAVING `doc_count` > @h0");

        var lookup = (SqlMapper.IParameterLookup)param;
        lookup["p0"].Should().Be("Alice");
        lookup["h0"].Should().Be(10.0);
    }

    // ── BuildAggregate — DateHistogram "quarter" ───────────────────────────────

    [Fact]
    public void BuildAggregate_DateHistogram_Quarter_UsesConcatAndQuarterFunction()
    {
        var spec = new SrAggSpec(
            "by_quarter", SrAggKind.DateHistogram, "PublishedAt",
            CalendarInterval: "quarter");

        var (sql, _) = StarRocksQueryBuilder.BuildAggregate("authors", AuthorSchema(), null, spec);

        sql.Should().Contain("CONCAT(YEAR(");
        sql.Should().Contain("QUARTER(");
        sql.Should().Contain("bucket_key");
    }

    [Theory]
    [InlineData("minute", "%Y-%m-%d %H:%i")]
    [InlineData("hour",   "%Y-%m-%d %H")]
    [InlineData("day",    "%Y-%m-%d")]
    [InlineData("week",   "%Y-%u")]
    [InlineData("month",  "%Y-%m")]
    [InlineData("year",   "%Y")]
    [InlineData("bogus-unrecognized-interval", "%Y-%m")]  // falls back to month's format
    public void BuildAggregate_DateHistogram_EachCalendarInterval_UsesExpectedDateFormat(
        string interval, string expectedFormat)
    {
        var spec = new SrAggSpec(
            "by_interval", SrAggKind.DateHistogram, "PublishedAt",
            CalendarInterval: interval);

        var (sql, _) = StarRocksQueryBuilder.BuildAggregate("authors", AuthorSchema(), null, spec);

        sql.Should().Contain($"DATE_FORMAT(`PublishedAt`, '{expectedFormat}')");
    }

    // ── BuildAggregate — joins ─────────────────────────────────────────────────

    [Fact]
    public void BuildAggregate_WithJoin_ProducesJoinAndQuotesJoinedColumn()
    {
        var registry = BuildRegistry(AuthorSchema(), SchemaFixtures.ArticleSchema());

        var joins = new List<JoinSpec>
        {
            new()
            {
                LeftType   = "Author",
                RightType  = "Article",
                LeftField  = "Id",
                RightField = "Id",
                Kind       = JoinKind.Inner
            }
        };

        var spec = new SrAggSpec("by_title", SrAggKind.Terms, "Article.Title", Size: 5);

        var (sql, _) = StarRocksQueryBuilder.BuildAggregate(
            "authors", AuthorSchema(), null, spec, joins: joins, registry: registry);

        sql.Should().Contain("FROM `authors` INNER JOIN `articles` ON `authors`.`Id` = `articles`.`Id`");
        sql.Should().Contain("SELECT `articles`.`Title` AS bucket_key");
        sql.Should().Contain("GROUP BY `articles`.`Title`");
    }

    [Fact]
    public void BuildAggregate_WithJoin_WhereOnJoinedTableColumn_QuotesAliasAndFieldSeparately()
    {
        var registry = BuildRegistry(AuthorSchema(), SchemaFixtures.ArticleSchema());

        var joins = new List<JoinSpec>
        {
            new()
            {
                LeftType   = "Author",
                RightType  = "Article",
                LeftField  = "Id",
                RightField = "Id",
                Kind       = JoinKind.Inner
            }
        };

        var query = new SearchQuery();
        query.Clauses.Add(new SearchClause
        {
            Property   = "Article.Title",
            Operator   = SearchOperator.Equals,
            Value      = new SearchValue { StringVal = "Foo" },
            ClauseType = SearchClauseType.Filter
        });

        var spec = new SrAggSpec("avg_rating", SrAggKind.Avg, "Rating");

        var (sql, param) = StarRocksQueryBuilder.BuildAggregate(
            "authors", AuthorSchema(), query, spec, joins: joins, registry: registry);

        sql.Should().Contain("WHERE `articles`.`Title` = @p0");
        sql.Should().Contain("AVG(`authors`.`Rating`)");

        var lookup = (SqlMapper.IParameterLookup)param;
        lookup["p0"].Should().Be("Foo");
    }

    [Fact]
    public void BuildAggregate_Min_BareField_ProducesMinFunction()
    {
        var spec = new SrAggSpec("lowest_rating", SrAggKind.Min, "Rating");

        var (sql, _) = StarRocksQueryBuilder.BuildAggregate("authors", AuthorSchema(), null, spec);

        sql.Should().Contain("MIN(`Rating`)");
    }

    [Fact]
    public void BuildAggregate_Max_BareField_ProducesMaxFunction()
    {
        var spec = new SrAggSpec("highest_rating", SrAggKind.Max, "Rating");

        var (sql, _) = StarRocksQueryBuilder.BuildAggregate("authors", AuthorSchema(), null, spec);

        sql.Should().Contain("MAX(`Rating`)");
    }

    [Fact]
    public void BuildAggregate_Count_BareField_ProducesCountDistinctFunction()
    {
        var spec = new SrAggSpec("distinct_ratings", SrAggKind.Count, "Rating");

        var (sql, _) = StarRocksQueryBuilder.BuildAggregate("authors", AuthorSchema(), null, spec);

        // BuildAggregate's bare-field Count path emits COUNT(DISTINCT ...), not plain COUNT(...) —
        // a real, non-obvious detail worth locking in with a test (StarRocksQueryBuilder.cs:162).
        sql.Should().Contain("COUNT(DISTINCT `Rating`)");
    }

    // ── BuildSearch — Equals clause (parameterization) ─────────────────────────

    [Fact]
    public void BuildSearch_EqualsClause_ParameterizesValue()
    {
        var query = new SearchQuery();
        query.Clauses.Add(new SearchClause
        {
            Property   = "Name",
            Operator   = SearchOperator.Equals,
            Value      = new SearchValue { StringVal = "Alice" },
            ClauseType = SearchClauseType.Filter
        });

        var (sql, param) = StarRocksQueryBuilder.BuildSearch("authors", AuthorSchema(), query, 0, 10);

        sql.Should().Contain("WHERE");
        sql.Should().Contain("`Name` = @p0");
        // Value is in DynamicParameters, not inlined in the SQL
        sql.Should().NotContain("Alice");
        // Verify the parameter exists (DynamicParameters lookup)
        var lookup = (SqlMapper.IParameterLookup)param;
        lookup["p0"].Should().Be("Alice");
    }

    // ── BuildSearch — Contains clause ─────────────────────────────────────────

    [Fact]
    public void BuildSearch_ContainsClause_ProducesLikeWithWildcards()
    {
        var query = new SearchQuery();
        query.Clauses.Add(new SearchClause
        {
            Property   = "Bio",
            Operator   = SearchOperator.Contains,
            Value      = new SearchValue { StringVal = "fiction" },
            ClauseType = SearchClauseType.Filter
        });

        var (sql, param) = StarRocksQueryBuilder.BuildSearch("authors", AuthorSchema(), query, 0, 10);

        sql.Should().Contain("LIKE @p0");
        var lookup = (SqlMapper.IParameterLookup)param;
        lookup["p0"].Should().Be("%fiction%");
    }

    // ── BuildSearch — EndsWith clause ────────────────────────────────────────

    [Fact]
    public void BuildSearch_EndsWithClause_ProducesLikeWithLeadingWildcard()
    {
        var query = new SearchQuery();
        query.Clauses.Add(new SearchClause
        {
            Property   = "Bio",
            Operator   = SearchOperator.EndsWith,
            Value      = new SearchValue { StringVal = "BRASS" },
            ClauseType = SearchClauseType.Filter
        });

        var (sql, param) = StarRocksQueryBuilder.BuildSearch("authors", AuthorSchema(), query, 0, 10);

        sql.Should().Contain("LIKE @p0");
        var lookup = (SqlMapper.IParameterLookup)param;
        lookup["p0"].Should().Be("%BRASS");
    }

    // ── BuildSearch — MustNot wraps with NOT ──────────────────────────────────

    [Fact]
    public void BuildSearch_MustNotClause_WrapsConditionWithNot()
    {
        var query = new SearchQuery();
        query.Clauses.Add(new SearchClause
        {
            Property   = "Name",
            Operator   = SearchOperator.Equals,
            Value      = new SearchValue { StringVal = "Bot" },
            ClauseType = SearchClauseType.MustNot
        });

        var (sql, _) = StarRocksQueryBuilder.BuildSearch("authors", AuthorSchema(), query, 0, 10);

        sql.Should().Contain("NOT (");
        sql.Should().Contain("`Name` = @p0");
    }

    // ── BuildSearch — In clause ───────────────────────────────────────────────

    [Fact]
    public void BuildSearch_InClause_ProducesInExpression()
    {
        var strList = new RepeatedString();
        strList.Values.Add("Alice");
        strList.Values.Add("Bob");

        var query = new SearchQuery();
        query.Clauses.Add(new SearchClause
        {
            Property   = "Name",
            Operator   = SearchOperator.In,
            Value      = new SearchValue { StringList = strList },
            ClauseType = SearchClauseType.Filter
        });

        var (sql, _) = StarRocksQueryBuilder.BuildSearch("authors", AuthorSchema(), query, 0, 10);

        sql.Should().Contain("`Name` IN @p0");
    }

    // ── BuildSearch — NotEquals clause ─────────────────────────────────────────

    [Fact]
    public void BuildSearch_NotEqualsClause_ProducesNotEqualsCondition()
    {
        var query = new SearchQuery();
        query.Clauses.Add(new SearchClause
        {
            Property   = "Name",
            Operator   = SearchOperator.NotEquals,
            Value      = new SearchValue { StringVal = "Alice" },
            ClauseType = SearchClauseType.Filter
        });

        var (sql, param) = StarRocksQueryBuilder.BuildSearch("authors", AuthorSchema(), query, 0, 50);

        sql.Should().Contain("WHERE `Name` <> @p0");
        var lookup = (SqlMapper.IParameterLookup)param;
        lookup["p0"].Should().Be("Alice");
    }

    // ── BuildSearch — StartsWith clause ────────────────────────────────────────

    [Fact]
    public void BuildSearch_StartsWithClause_ProducesLikeWithTrailingWildcard()
    {
        var query = new SearchQuery();
        query.Clauses.Add(new SearchClause
        {
            Property   = "Name",
            Operator   = SearchOperator.StartsWith,
            Value      = new SearchValue { StringVal = "Al" },
            ClauseType = SearchClauseType.Filter
        });

        var (sql, param) = StarRocksQueryBuilder.BuildSearch("authors", AuthorSchema(), query, 0, 50);

        sql.Should().Contain("WHERE `Name` LIKE @p0");
        var lookup = (SqlMapper.IParameterLookup)param;
        lookup["p0"].Should().Be("Al%");
    }

    // ── BuildSearch — GreaterThanOrEquals clause ───────────────────────────────

    [Fact]
    public void BuildSearch_GreaterThanOrEqualsClause_ProducesGteCondition()
    {
        var query = new SearchQuery();
        query.Clauses.Add(new SearchClause
        {
            Property   = "Rating",
            Operator   = SearchOperator.GreaterThanOrEquals,
            Value      = new SearchValue { NumberVal = 4 },
            ClauseType = SearchClauseType.Filter
        });

        var (sql, param) = StarRocksQueryBuilder.BuildSearch("authors", AuthorSchema(), query, 0, 50);

        sql.Should().Contain("WHERE `Rating` >= @p0");
        var lookup = (SqlMapper.IParameterLookup)param;
        lookup["p0"].Should().Be(4.0);
    }

    // ── BuildSearch — LessThan clause ──────────────────────────────────────────

    [Fact]
    public void BuildSearch_LessThanClause_ProducesLtCondition()
    {
        var query = new SearchQuery();
        query.Clauses.Add(new SearchClause
        {
            Property   = "Rating",
            Operator   = SearchOperator.LessThan,
            Value      = new SearchValue { NumberVal = 3 },
            ClauseType = SearchClauseType.Filter
        });

        var (sql, param) = StarRocksQueryBuilder.BuildSearch("authors", AuthorSchema(), query, 0, 50);

        sql.Should().Contain("WHERE `Rating` < @p0");
        var lookup = (SqlMapper.IParameterLookup)param;
        lookup["p0"].Should().Be(3.0);
    }

    // ── BuildSearch — LessThanOrEquals clause ──────────────────────────────────

    [Fact]
    public void BuildSearch_LessThanOrEqualsClause_ProducesLteCondition()
    {
        var query = new SearchQuery();
        query.Clauses.Add(new SearchClause
        {
            Property   = "Rating",
            Operator   = SearchOperator.LessThanOrEquals,
            Value      = new SearchValue { NumberVal = 3 },
            ClauseType = SearchClauseType.Filter
        });

        var (sql, param) = StarRocksQueryBuilder.BuildSearch("authors", AuthorSchema(), query, 0, 50);

        sql.Should().Contain("WHERE `Rating` <= @p0");
        var lookup = (SqlMapper.IParameterLookup)param;
        lookup["p0"].Should().Be(3.0);
    }

    // ── BuildSearch — VectorSimilar behavior ───────────────────────────────────

    [Fact]
    public void BuildSearch_VectorSimilarClause_ThrowsInvalidArgument()
    {
        var query = new SearchQuery();
        query.Clauses.Add(new SearchClause
        {
            Property   = "Bio",
            Operator   = SearchOperator.VectorSimilar,
            Value      = new SearchValue { StringVal = "some query text" },
            ClauseType = SearchClauseType.Filter
        });

        var act = () => StarRocksQueryBuilder.BuildSearch("authors", AuthorSchema(), query, 0, 50);

        // VECTOR_SIMILAR is Qdrant's job, not StarRocks's — BuildSearch (via BuildWhere)
        // must reject it loudly with InvalidArgument, not silently drop it.
        act.Should().Throw<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument
                     && e.Status.Detail.Contains("VECTOR_SIMILAR"));
    }

    // ── BuildSearch — explicit SELECT columns ─────────────────────────────────────

    [Fact]
    public void BuildSearch_NoFields_SelectsAllColumnsExplicitly()
    {
        var schema = SchemaFixtures.ArticleWithProjectionSchema();

        var (sql, _) = StarRocksQueryBuilder.BuildSearch("articles", schema, null, 0, 10);

        sql.Should().StartWith("SELECT ");
        sql.Should().NotContain("SELECT *");
        sql.Should().Contain("`Id`");
        sql.Should().Contain("`Title`");
        sql.Should().Contain("`Body`");
        sql.Should().Contain("`Category`");
        sql.Should().Contain("`PublishedAt`");
    }

    [Fact]
    public void BuildSearch_WithFields_SelectsOnlyRequestedColumnsAndKey()
    {
        var schema = SchemaFixtures.ArticleWithProjectionSchema();

        var (sql, _) = StarRocksQueryBuilder.BuildSearch(
            "articles", schema, null, 0, 10,
            fields: ["Category", "PublishedAt", "Title"]);

        sql.Should().Contain("`Id`");
        sql.Should().Contain("`Category`");
        sql.Should().Contain("`PublishedAt`");
        sql.Should().Contain("`Title`");
        sql.Should().NotContain("`Body`");
        sql.Should().NotContain("`WordCount`");
    }

    [Fact]
    public void BuildSearch_WithFields_KeyAlwaysIncludedEvenIfNotRequested()
    {
        var schema = SchemaFixtures.ArticleWithProjectionSchema();

        var (sql, _) = StarRocksQueryBuilder.BuildSearch(
            "articles", schema, null, 0, 10,
            fields: ["Category"]);

        sql.Should().Contain("`Id`");
        sql.Should().Contain("`Category`");
        sql.Should().NotContain("`Body`");
    }

    [Fact]
    public void BuildSearch_WithFields_UnknownFieldNamesAreIgnored()
    {
        var schema = SchemaFixtures.ArticleWithProjectionSchema();

        var (sql, _) = StarRocksQueryBuilder.BuildSearch(
            "articles", schema, null, 0, 10,
            fields: ["Category", "NonExistentField"]);

        sql.Should().Contain("`Id`");
        sql.Should().Contain("`Category`");
        sql.Should().NotContain("NonExistentField");
    }

    // ── BuildSearch — joins ────────────────────────────────────────────────────

    [Fact]
    public void BuildSearch_WithJoin_ProducesInnerJoin()
    {
        var registry = BuildRegistry(AuthorSchema(), SchemaFixtures.ArticleSchema());

        var joins = new List<JoinSpec>
        {
            new()
            {
                LeftType   = "Author",
                RightType  = "Article",
                LeftField  = "Id",
                RightField = "Id",
                Kind       = JoinKind.Inner
            }
        };

        var (sql, _) = StarRocksQueryBuilder.BuildSearch(
            "authors", AuthorSchema(), null, 0, 10,
            joins: joins, registry: registry);

        sql.Should().Contain("FROM `authors` INNER JOIN `articles` ON `authors`.`Id` = `articles`.`Id`");
    }

    [Fact]
    public void BuildSearch_WithJoin_WhereOnJoinedTableColumn_QuotesAliasAndFieldSeparately()
    {
        var registry = BuildRegistry(AuthorSchema(), SchemaFixtures.ArticleSchema());

        var joins = new List<JoinSpec>
        {
            new()
            {
                LeftType   = "Author",
                RightType  = "Article",
                LeftField  = "Id",
                RightField = "Id",
                Kind       = JoinKind.Inner
            }
        };

        var query = new SearchQuery();
        query.Clauses.Add(new SearchClause
        {
            Property   = "Article.Title",
            Operator   = SearchOperator.Equals,
            Value      = new SearchValue { StringVal = "Foo" },
            ClauseType = SearchClauseType.Filter
        });

        var (sql, param) = StarRocksQueryBuilder.BuildSearch(
            "authors", AuthorSchema(), query, 0, 10,
            joins: joins, registry: registry);

        // Regression test: the joined-column WHERE fragment must be two separately-quoted
        // identifiers ("`articles`.`Title`"), never one backtick pair around the whole
        // "articles.Title" string — the latter is invalid SQL in the MySQL-wire dialect
        // (see QuoteQualified's doc comment / Task 4 history).
        sql.Should().Contain("`articles`.`Title` = @p0");
        sql.Should().NotContain("`articles.Title`");

        var lookup = (SqlMapper.IParameterLookup)param;
        lookup["p0"].Should().Be("Foo");
    }

    [Fact]
    public void BuildSearch_NoJoins_WhereClauseUsesSingleBacktickColumn()
    {
        var query = new SearchQuery();
        query.Clauses.Add(new SearchClause
        {
            Property   = "Name",
            Operator   = SearchOperator.Equals,
            Value      = new SearchValue { StringVal = "Alice" },
            ClauseType = SearchClauseType.Filter
        });

        var (sql, _) = StarRocksQueryBuilder.BuildSearch("authors", AuthorSchema(), query, 0, 10);

        // No-join path is unchanged: bare single-backtick column, no alias qualification.
        sql.Should().Contain("`Name` = @p0");
        sql.Should().Contain("FROM `authors`");
        sql.Should().NotContain("`authors`.`Name`");
    }

    // ── BuildFromWithJoins ─────────────────────────────────────────────────────

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
    public void BuildFromWithJoins_InnerJoin_ProducesJoinClause()
    {
        var registry = BuildRegistry(AuthorSchema(), SchemaFixtures.ArticleSchema());

        var joins = new List<JoinSpec>
        {
            new()
            {
                LeftType   = "Author",
                RightType  = "Article",
                LeftField  = "Id",
                RightField = "Id",
                Kind       = JoinKind.Inner
            }
        };

        var from = StarRocksQueryBuilder.BuildFromWithJoins(AuthorSchema(), joins, registry, out var tableMap);

        from.Should().Be("FROM `authors` INNER JOIN `articles` ON `authors`.`Id` = `articles`.`Id`");
        tableMap.Should().ContainKey("Author");
        tableMap.Should().ContainKey("Article");
        tableMap["Author"].TableName.Should().Be("authors");
        tableMap["Article"].TableName.Should().Be("articles");
    }

    [Fact]
    public void BuildFromWithJoins_LeftJoin_ProducesLeftJoinClause()
    {
        var registry = BuildRegistry(AuthorSchema(), SchemaFixtures.ArticleSchema());

        var joins = new List<JoinSpec>
        {
            new()
            {
                LeftType   = "Author",
                RightType  = "Article",
                LeftField  = "Id",
                RightField = "Id",
                Kind       = JoinKind.Left
            }
        };

        var from = StarRocksQueryBuilder.BuildFromWithJoins(AuthorSchema(), joins, registry, out _);

        from.Should().Contain("LEFT JOIN `articles`");
        from.Should().NotContain("INNER JOIN");
    }

    [Fact]
    public void BuildFromWithJoins_RightJoin_ProducesRightJoinClause()
    {
        var registry = BuildRegistry(AuthorSchema(), SchemaFixtures.ArticleSchema());

        var joins = new List<JoinSpec>
        {
            new()
            {
                LeftType   = "Author",
                RightType  = "Article",
                LeftField  = "Id",
                RightField = "Id",
                Kind       = JoinKind.Right
            }
        };

        var from = StarRocksQueryBuilder.BuildFromWithJoins(AuthorSchema(), joins, registry, out _);

        from.Should().Contain("RIGHT JOIN `articles`");
        from.Should().NotContain("INNER JOIN");
    }

    [Fact]
    public void BuildFromWithJoins_FullJoin_ProducesJoinClause()
    {
        var registry = BuildRegistry(AuthorSchema(), SchemaFixtures.ArticleSchema());

        var joins = new List<JoinSpec>
        {
            new()
            {
                LeftType   = "Author",
                RightType  = "Article",
                LeftField  = "Id",
                RightField = "Id",
                Kind       = JoinKind.Full
            }
        };

        var from = StarRocksQueryBuilder.BuildFromWithJoins(AuthorSchema(), joins, registry, out var tableMap);

        from.Should().Be("FROM `authors` FULL JOIN `articles` ON `authors`.`Id` = `articles`.`Id`");
        tableMap.Should().ContainKey("Author");
        tableMap.Should().ContainKey("Article");
    }

    [Fact]
    public void BuildFromWithJoins_UnknownType_ThrowsInvalidArgument()
    {
        var registry = BuildRegistry(AuthorSchema());

        var joins = new List<JoinSpec>
        {
            new()
            {
                LeftType   = "Author",
                RightType  = "NoSuchType",
                LeftField  = "Id",
                RightField = "AuthorId",
                Kind       = JoinKind.Inner
            }
        };

        var act = () => StarRocksQueryBuilder.BuildFromWithJoins(AuthorSchema(), joins, registry, out _);

        act.Should().Throw<RpcException>()
            .Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public void BuildFromWithJoins_NoJoins_StillPopulatesTableMapWithPrimaryTable()
    {
        var registry = BuildRegistry(AuthorSchema());

        var from = StarRocksQueryBuilder.BuildFromWithJoins(AuthorSchema(), [], registry, out var tableMap);

        from.Should().Be("FROM `authors`");
        tableMap.Should().ContainKey("Author");
        tableMap["Author"].TableName.Should().Be("authors");
        tableMap["Author"].Alias.Should().Be("authors");
    }

    [Fact]
    public void ResolveColumn_JoinContext_DotNotationResolves()
    {
        var registry = BuildRegistry(AuthorSchema(), SchemaFixtures.ArticleSchema());

        var joins = new List<JoinSpec>
        {
            new()
            {
                LeftType   = "Author",
                RightType  = "Article",
                LeftField  = "Id",
                RightField = "Id",
                Kind       = JoinKind.Inner
            }
        };

        StarRocksQueryBuilder.BuildFromWithJoins(AuthorSchema(), joins, registry, out var tableMap);

        var col = StarRocksQueryBuilder.ResolveColumn(tableMap, "Article.Title");

        col.Should().Be("articles.Title");
    }

    // ── BuildGroupBy ───────────────────────────────────────────────────────────

    private static GroupByRequest Q1StyleRequest()
    {
        var request = new GroupByRequest
        {
            TypeName = "Author",
            Keys     = { "Name", "Rating" },
            Limit    = 100
        };
        request.Metrics.Add(new MetricSpec { Name = "sum_rating",   Type = AggregationType.Sum,   Field = "Rating" });
        request.Metrics.Add(new MetricSpec { Name = "avg_rating",   Type = AggregationType.Avg,   Field = "Rating" });
        request.Metrics.Add(new MetricSpec { Name = "min_rating",   Type = AggregationType.Min,   Field = "Rating" });
        request.Metrics.Add(new MetricSpec { Name = "max_rating",   Type = AggregationType.Max,   Field = "Rating" });
        request.Metrics.Add(new MetricSpec { Name = "count_rating", Type = AggregationType.Count, Field = "Rating" });
        request.Metrics.Add(new MetricSpec { Name = "count_star",   Type = AggregationType.Count });
        request.Metrics.Add(new MetricSpec { Name = "net_rating",   Type = AggregationType.Sum,   Expression = "Rating * (1 - 0)" });
        request.Metrics.Add(new MetricSpec { Name = "charge",       Type = AggregationType.Sum,   Expression = "Rating * (1 - 0) * (1 + 0)" });
        request.Metrics.Add(new MetricSpec { Name = "avg_name_len", Type = AggregationType.Avg,   Expression = "LENGTH(Name)" });
        request.OrderBy.Add(new SearchSort { Property = "Name" });
        request.OrderBy.Add(new SearchSort { Property = "Rating", Descending = true });
        return request;
    }

    [Fact]
    public void BuildGroupBy_SingleTable_ProducesCompoundSelect()
    {
        var registry = BuildRegistry(AuthorSchema());
        var request  = Q1StyleRequest();

        var (sql, _) = StarRocksQueryBuilder.BuildGroupBy("authors", AuthorSchema(), request, registry);

        // Columns now resolve via the multi-schema tableMap path (always populated, even with
        // no joins), so they're consistently alias-qualified as `authors`.`Field` — matching the
        // convention already used by BuildWhere/BuildFromWithJoins for joined columns. Each part
        // is separately backtick-quoted (not one backtick pair around "authors.Field") since a
        // backtick-quoted token does not split on '.' in MySQL-wire SQL.
        sql.Should().Contain("SELECT `authors`.`Name`, `authors`.`Rating`");
        sql.Should().Contain("SUM(`authors`.`Rating`) AS `sum_rating`");
        sql.Should().Contain("AVG(`authors`.`Rating`) AS `avg_rating`");
        sql.Should().Contain("MIN(`authors`.`Rating`) AS `min_rating`");
        sql.Should().Contain("MAX(`authors`.`Rating`) AS `max_rating`");
        sql.Should().Contain("COUNT(`authors`.`Rating`) AS `count_rating`");
        sql.Should().Contain("COUNT(*) AS `count_star`");
        sql.Should().Contain("SUM(Rating * (1 - 0)) AS `net_rating`");
        sql.Should().Contain("SUM(Rating * (1 - 0) * (1 + 0)) AS `charge`");
        sql.Should().Contain("AVG(LENGTH(Name)) AS `avg_name_len`");
        sql.Should().Contain("FROM `authors`");
        sql.Should().Contain("GROUP BY `authors`.`Name`, `authors`.`Rating`");
        sql.Should().Contain("ORDER BY `authors`.`Name` ASC, `authors`.`Rating` DESC");
        sql.Should().Contain("LIMIT 100");
    }

    [Fact]
    public void BuildGroupBy_WithJoin_ProducesJoinClause()
    {
        var registry = BuildRegistry(AuthorSchema(), SchemaFixtures.ArticleSchema());

        var request = new GroupByRequest
        {
            TypeName = "Author",
            Keys     = { "Article.Title" },
            Limit    = 50,
            Joins =
            {
                new JoinSpec
                {
                    LeftType   = "Author",
                    RightType  = "Article",
                    LeftField  = "Id",
                    RightField = "Id",
                    Kind       = JoinKind.Inner
                }
            }
        };
        request.Metrics.Add(new MetricSpec { Name = "cnt", Type = AggregationType.Count });

        var (sql, _) = StarRocksQueryBuilder.BuildGroupBy("authors", AuthorSchema(), request, registry);

        sql.Should().Contain("FROM `authors` INNER JOIN `articles` ON `authors`.`Id` = `articles`.`Id`");
        sql.Should().Contain("SELECT `articles`.`Title`");
        sql.Should().Contain("GROUP BY `articles`.`Title`");
    }

    [Fact]
    public void BuildGroupBy_ExpressionMetric_PassesRawExpression()
    {
        var registry = BuildRegistry(AuthorSchema());

        var request = new GroupByRequest
        {
            TypeName = "Author",
            Keys     = { "Name" },
        };
        request.Metrics.Add(new MetricSpec
        {
            Name       = "revenue",
            Type       = AggregationType.Sum,
            Expression = "Rating * 2"
        });

        var (sql, _) = StarRocksQueryBuilder.BuildGroupBy("authors", AuthorSchema(), request, registry);

        sql.Should().Contain("SUM(Rating * 2) AS `revenue`");
        sql.Should().NotContain("SUM(`Rating`)");
    }

    [Fact]
    public void BuildGroupBy_Having_AppendsHavingClause()
    {
        var registry = BuildRegistry(AuthorSchema());

        var request = new GroupByRequest
        {
            TypeName = "Author",
            Keys     = { "Name" },
        };
        request.Metrics.Add(new MetricSpec { Name = "cnt", Type = AggregationType.Count });
        request.Having = new SearchQuery();
        request.Having.Clauses.Add(new SearchClause
        {
            Property   = "cnt",
            Operator   = SearchOperator.GreaterThan,
            Value      = new SearchValue { NumberVal = 300 },
            ClauseType = SearchClauseType.Filter
        });

        var (sql, param) = StarRocksQueryBuilder.BuildGroupBy("authors", AuthorSchema(), request, registry);

        sql.Should().Contain("HAVING `cnt` > @h0");
        sql.IndexOf("GROUP BY", StringComparison.Ordinal)
            .Should().BeLessThan(sql.IndexOf("HAVING", StringComparison.Ordinal));

        var lookup = (SqlMapper.IParameterLookup)param;
        lookup["h0"].Should().Be(300.0);
    }

    [Fact]
    public void BuildGroupBy_CountAll_EmitsCountStar()
    {
        var registry = BuildRegistry(AuthorSchema());

        var request = new GroupByRequest
        {
            TypeName = "Author",
            Keys     = { "Name" },
        };
        request.Metrics.Add(new MetricSpec { Name = "cnt", Type = AggregationType.Count });

        var (sql, _) = StarRocksQueryBuilder.BuildGroupBy("authors", AuthorSchema(), request, registry);

        sql.Should().Contain("COUNT(*) AS `cnt`");
    }

    [Fact]
    public void BuildGroupBy_MetricNameWithBacktick_EscapesEmbeddedBacktick()
    {
        var registry = BuildRegistry(AuthorSchema());

        var request = new GroupByRequest
        {
            TypeName = "Author",
            Keys     = { "Name" },
        };
        request.Metrics.Add(new MetricSpec { Name = "evil`name", Type = AggregationType.Count });

        var (sql, _) = StarRocksQueryBuilder.BuildGroupBy("authors", AuthorSchema(), request, registry);

        // An embedded backtick in a developer-supplied metric name must be escaped
        // (doubled), not spliced in raw — otherwise it breaks out of the identifier.
        sql.Should().Contain("AS `evil``name`");
    }

    [Fact]
    public void BuildGroupBy_HavingPropertyWithBacktick_EscapesEmbeddedBacktick()
    {
        var registry = BuildRegistry(AuthorSchema());

        var request = new GroupByRequest
        {
            TypeName = "Author",
            Keys     = { "Name" },
        };
        request.Metrics.Add(new MetricSpec { Name = "cnt", Type = AggregationType.Count });
        request.Having = new SearchQuery();
        request.Having.Clauses.Add(new SearchClause
        {
            Property   = "evil`alias",
            Operator   = SearchOperator.GreaterThan,
            Value      = new SearchValue { NumberVal = 1 },
            ClauseType = SearchClauseType.Filter
        });

        var (sql, _) = StarRocksQueryBuilder.BuildGroupBy("authors", AuthorSchema(), request, registry);

        sql.Should().Contain("HAVING `evil``alias` > @h0");
    }

    [Fact]
    public void BuildGroupBy_NonCountMetricWithNoFieldOrExpression_ThrowsInvalidArgument()
    {
        var registry = BuildRegistry(AuthorSchema());

        var request = new GroupByRequest
        {
            TypeName = "Author",
            Keys     = { "Name" },
        };
        request.Metrics.Add(new MetricSpec { Name = "bad_sum", Type = AggregationType.Sum });

        var act = () => StarRocksQueryBuilder.BuildGroupBy("authors", AuthorSchema(), request, registry);

        act.Should().Throw<RpcException>()
            .Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public void BuildGroupBy_WithJoin_WhereOnJoinedTableColumn_QuotesAliasAndFieldSeparately()
    {
        var registry = BuildRegistry(AuthorSchema(), SchemaFixtures.ArticleSchema());

        var request = new GroupByRequest
        {
            TypeName = "Author",
            Keys     = { "Name" },
            Limit    = 50,
            Query    = new SearchQuery(),
            Joins =
            {
                new JoinSpec
                {
                    LeftType   = "Author",
                    RightType  = "Article",
                    LeftField  = "Id",
                    RightField = "Id",
                    Kind       = JoinKind.Inner
                }
            }
        };
        request.Query.Clauses.Add(new SearchClause
        {
            Property   = "Article.Title",
            Operator   = SearchOperator.Equals,
            Value      = new SearchValue { StringVal = "Foo" },
            ClauseType = SearchClauseType.Filter
        });
        request.Metrics.Add(new MetricSpec { Name = "cnt", Type = AggregationType.Count });

        var (sql, param) = StarRocksQueryBuilder.BuildGroupBy("authors", AuthorSchema(), request, registry);

        // Regression test: BuildGroupBy must pass tableMap into BuildWhere so joined-table
        // WHERE filters aren't silently dropped, and the resulting fragment must be two
        // separately-quoted identifiers, never one backtick pair around "articles.Title".
        sql.Should().Contain("WHERE");
        sql.Should().Contain("`articles`.`Title` = @p0");
        sql.Should().NotContain("`articles.Title`");

        var lookup = (SqlMapper.IParameterLookup)param;
        lookup["p0"].Should().Be("Foo");
    }

    // ── BuildWhere/BuildHaving — resolver + prefix overloads ──────────────────

    [Fact]
    public void BuildWhere_ResolverOverload_UsesPrefixAndResolver()
    {
        var param = new DynamicParameters();
        var clauses = new[]
        {
            new SearchClause
            {
                Property = "articles", Operator = SearchOperator.GreaterThan,
                Value = new SearchValue { NumberVal = 5 }, ClauseType = SearchClauseType.Filter
            }
        };

        var sql = StarRocksQueryBuilder.BuildWhere(
            p => p == "articles" ? "`articles`" : null,
            clauses, SearchLogic.And, param, "s2_p", out var next);

        sql.Should().Be("`articles` > @s2_p0");
        next.Should().Be(1);
        param.Get<double>("s2_p0").Should().Be(5);
    }

    [Fact]
    public void BuildWhere_ResolverOverload_SkipsUnresolvableColumns()
    {
        var param = new DynamicParameters();
        var clauses = new[]
        {
            new SearchClause
            {
                Property = "nope", Operator = SearchOperator.Equals,
                Value = new SearchValue { NumberVal = 1 }, ClauseType = SearchClauseType.Filter
            }
        };

        var sql = StarRocksQueryBuilder.BuildWhere(
            _ => null, clauses, SearchLogic.And, param, "s1_p", out _);

        sql.Should().BeEmpty();
    }

    [Fact]
    public void BuildHaving_PrefixOverload_UsesPrefix()
    {
        var param = new DynamicParameters();
        var clauses = new[]
        {
            new SearchClause
            {
                Property = "article_count", Operator = SearchOperator.GreaterThan,
                Value = new SearchValue { NumberVal = 3 }, ClauseType = SearchClauseType.Filter
            }
        };

        var sql = StarRocksQueryBuilder.BuildHaving(clauses, SearchLogic.And, param, "s3_h");

        sql.Should().Be("`article_count` > @s3_h0");
        param.Get<double>("s3_h0").Should().Be(3);
    }

    // ── VectorSimilar rejection ────────────────────────────────────────────────

    private static SearchClause VectorClause() => new()
    {
        Property   = "Name",
        Operator   = SearchOperator.VectorSimilar,
        Value      = new SearchValue { FloatList = new RepeatedFloat { Values = { 0.1f, 0.2f } } },
        ClauseType = SearchClauseType.Filter
    };

    [Fact]
    public void BuildWhere_VectorSimilarClause_ThrowsInvalidArgument()
    {
        var param = new DynamicParameters();
        var act = () => StarRocksQueryBuilder.BuildWhere(
            AuthorSchema(), [VectorClause()], SearchLogic.And, param, out _);

        act.Should().Throw<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument
                     && e.Status.Detail.Contains("VECTOR_SIMILAR")
                     && e.Status.Detail.Contains("SearchSimilar"));
    }

    [Fact]
    public void BuildHaving_VectorSimilarClause_ThrowsInvalidArgument()
    {
        var param = new DynamicParameters();
        var act = () => StarRocksQueryBuilder.BuildHaving(
            [VectorClause()], SearchLogic.And, param);

        act.Should().Throw<RpcException>()
            .Where(e => e.Status.StatusCode == StatusCode.InvalidArgument);
    }
}
