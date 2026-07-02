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

        var from = StarRocksQueryBuilder.BuildFromWithJoins("authors", joins, registry, out var tableMap);

        from.Should().Be("FROM `authors` INNER JOIN `articles` ON `authors`.`Id` = `articles`.`Id`");
        tableMap.Should().ContainKey("Author");
        tableMap.Should().ContainKey("Article");
        tableMap["Author"].TableName.Should().Be("authors");
        tableMap["Article"].TableName.Should().Be("articles");
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

        var act = () => StarRocksQueryBuilder.BuildFromWithJoins("authors", joins, registry, out _);

        act.Should().Throw<RpcException>()
            .Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
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

        StarRocksQueryBuilder.BuildFromWithJoins("authors", joins, registry, out var tableMap);

        var col = StarRocksQueryBuilder.ResolveColumn(tableMap, "Article.Title");

        col.Should().Be("articles.Title");
    }
}
