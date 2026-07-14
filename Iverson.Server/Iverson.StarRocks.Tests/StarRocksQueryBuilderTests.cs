using Dapper;
using FluentAssertions;
using Iverson.Client.Contracts;
using Xunit;

namespace Iverson.StarRocks.Tests;

public class StarRocksQueryBuilderTests
{
    // ── Fixtures ───────────────────────────────────────────────────────────────

    private static StarRocksQuerySchema AuthorSchema() => new(
        "Author", "authors", "Id", ["Name", "Bio", "Rating", "PublishedAt"]);

    private static StarRocksQuerySchema ArticleSchema() => new(
        "Article", "articles", "Id", ["Title", "Body"]);

    private static StarRocksQuerySchema ArticleWithProjectionSchema() => new(
        "Article", "articles", "Id", ["Title", "Category", "WordCount", "PublishedAt", "Body"]);

    // ── BuildAggregate — Terms ─────────────────────────────────────────────────

    [Fact]
    public void BuildAggregate_Terms_ProducesGroupByWithLimit()
    {
        var spec = new AggregationDescriptor("by_name", AggregationKind.Terms, "Name", Size: 5);

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
        var spec = new AggregationDescriptor(
            "rating_ranges", AggregationKind.Range, "Rating",
            RangeBuckets:
            [
                new RangeBucketDescriptor("low",  null, 3),
                new RangeBucketDescriptor("mid",  3,    7),
                new RangeBucketDescriptor("high", 7,    null),
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
        var spec = new AggregationDescriptor(
            "r", AggregationKind.Range, "Rating",
            RangeBuckets: [new RangeBucketDescriptor("it's high", 7, null)]);

        var (sql, _) = StarRocksQueryBuilder.BuildAggregate("authors", AuthorSchema(), null, spec);

        sql.Should().Contain("it''s high");
    }

    [Fact]
    public void BuildAggregate_Range_NoBuckets_AppendsHavingClause()
    {
        var spec = new AggregationDescriptor("rating_ranges", AggregationKind.Range, "Rating", RangeBuckets: null);

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
        var spec = new AggregationDescriptor(
            "by_name_rating", AggregationKind.Terms, "Name",
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
        var spec = new AggregationDescriptor(
            "revenue", AggregationKind.Sum, "Rating",
            Expression: "Rating * 2");

        var (sql, _) = StarRocksQueryBuilder.BuildAggregate("authors", AuthorSchema(), null, spec);

        sql.Should().Contain("SUM(Rating * 2)");
        sql.Should().NotContain("SUM(`Rating`)");
    }

    // ── BuildAggregate — HAVING ─────────────────────────────────────────────────

    [Fact]
    public void BuildAggregate_Having_AppendsHavingClause()
    {
        var spec = new AggregationDescriptor("by_name", AggregationKind.Terms, "Name", Size: 5);

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

        var spec = new AggregationDescriptor("by_name", AggregationKind.Terms, "Name", Size: 5);

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
        var spec = new AggregationDescriptor(
            "by_quarter", AggregationKind.DateHistogram, "PublishedAt",
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
        var spec = new AggregationDescriptor(
            "by_interval", AggregationKind.DateHistogram, "PublishedAt",
            CalendarInterval: interval);

        var (sql, _) = StarRocksQueryBuilder.BuildAggregate("authors", AuthorSchema(), null, spec);

        sql.Should().Contain($"DATE_FORMAT(`PublishedAt`, '{expectedFormat}')");
    }

    // ── BuildAggregate — joins ─────────────────────────────────────────────────

    [Fact]
    public void BuildAggregate_WithJoin_ProducesJoinAndQuotesJoinedColumn()
    {
        var registry = BuildRegistry(AuthorSchema(), ArticleSchema());

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

        var spec = new AggregationDescriptor("by_title", AggregationKind.Terms, "Article.Title", Size: 5);

        var (sql, _) = StarRocksQueryBuilder.BuildAggregate(
            "authors", AuthorSchema(), null, spec, joins: joins, registry: registry);

        sql.Should().Contain("FROM `authors` INNER JOIN `articles` ON `authors`.`Id` = `articles`.`Id`");
        sql.Should().Contain("SELECT `articles`.`Title` AS bucket_key");
        sql.Should().Contain("GROUP BY `articles`.`Title`");
    }

    [Fact]
    public void BuildAggregate_WithJoin_WhereOnJoinedTableColumn_QuotesAliasAndFieldSeparately()
    {
        var registry = BuildRegistry(AuthorSchema(), ArticleSchema());

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

        var spec = new AggregationDescriptor("avg_rating", AggregationKind.Avg, "Rating");

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
        var spec = new AggregationDescriptor("lowest_rating", AggregationKind.Min, "Rating");

        var (sql, _) = StarRocksQueryBuilder.BuildAggregate("authors", AuthorSchema(), null, spec);

        sql.Should().Contain("MIN(`Rating`)");
    }

    [Fact]
    public void BuildAggregate_Max_BareField_ProducesMaxFunction()
    {
        var spec = new AggregationDescriptor("highest_rating", AggregationKind.Max, "Rating");

        var (sql, _) = StarRocksQueryBuilder.BuildAggregate("authors", AuthorSchema(), null, spec);

        sql.Should().Contain("MAX(`Rating`)");
    }

    [Fact]
    public void BuildAggregate_Count_BareField_ProducesCountDistinctFunction()
    {
        var spec = new AggregationDescriptor("distinct_ratings", AggregationKind.Count, "Rating");

        var (sql, _) = StarRocksQueryBuilder.BuildAggregate("authors", AuthorSchema(), null, spec);

        // BuildAggregate's bare-field Count path emits COUNT(DISTINCT ...), not plain COUNT(...) —
        // a real, non-obvious detail worth locking in with a test (StarRocksQueryBuilder.cs:162).
        sql.Should().Contain("COUNT(DISTINCT `Rating`)");
    }

    // ── BuildAggregate — authorization (row ownership) ─────────────────────────

    [Fact]
    public void BuildAggregate_NoJoins_OwnerConstraint_WrapsExistingWhereWithOwnerPredicate()
    {
        var query = new SearchQuery();
        query.Clauses.Add(new SearchClause
        {
            Property = "Name", Operator = SearchOperator.Equals,
            Value = new SearchValue { StringVal = "Alice" }, ClauseType = SearchClauseType.Filter
        });
        var spec = new AggregationDescriptor("by_name", AggregationKind.Terms, "Name");
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Author"] = new(AllowedFields: null, OwnerColumn: "OwnerId", OwnerValue: "user-1")
        };

        var (sql, param) = StarRocksQueryBuilder.BuildAggregate(
            "authors", AuthorSchema(), query, spec, authz: authz);

        sql.Should().Contain("WHERE (`Name` = @p0) AND `OwnerId` = @__ownerVal");
        var lookup = (SqlMapper.IParameterLookup)param;
        lookup["__ownerVal"].Should().Be("user-1");
    }

    [Fact]
    public void BuildAggregate_NoJoins_OwnerConstraint_NoExistingWhere_UsesOwnerPredicateAlone()
    {
        var spec = new AggregationDescriptor("by_name", AggregationKind.Terms, "Name");
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Author"] = new(AllowedFields: null, OwnerColumn: "OwnerId", OwnerValue: "user-1")
        };

        var (sql, param) = StarRocksQueryBuilder.BuildAggregate(
            "authors", AuthorSchema(), null, spec, authz: authz);

        sql.Should().Contain("WHERE `OwnerId` = @__ownerVal");
        var lookup = (SqlMapper.IParameterLookup)param;
        lookup["__ownerVal"].Should().Be("user-1");
    }

    [Fact]
    public void BuildAggregate_NoJoins_NoOwnerColumn_DoesNotAddOwnerPredicate()
    {
        var spec = new AggregationDescriptor("by_name", AggregationKind.Terms, "Name");
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Author"] = new(AllowedFields: null, OwnerColumn: null, OwnerValue: null)
        };

        var (sql, _) = StarRocksQueryBuilder.BuildAggregate(
            "authors", AuthorSchema(), null, spec, authz: authz);

        sql.Should().NotContain("@__ownerVal");
        sql.Should().NotContain("WHERE");
    }

    [Fact]
    public void BuildAggregate_WithJoins_OwnerConstraint_QualifiesOwnerColumnWithPrimaryAlias()
    {
        var registry = BuildRegistry(AuthorSchema(), ArticleSchema());
        var joins = new List<JoinSpec>
        {
            new() { LeftType = "Author", RightType = "Article", LeftField = "Id", RightField = "Id", Kind = JoinKind.Inner }
        };
        var spec = new AggregationDescriptor("by_title", AggregationKind.Terms, "Article.Title");
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Author"] = new(AllowedFields: null, OwnerColumn: "OwnerId", OwnerValue: "user-1")
        };

        var (sql, param) = StarRocksQueryBuilder.BuildAggregate(
            "authors", AuthorSchema(), null, spec, joins: joins, registry: registry, authz: authz);

        sql.Should().Contain("WHERE `authors`.`OwnerId` = @__ownerVal");
        var lookup = (SqlMapper.IParameterLookup)param;
        lookup["__ownerVal"].Should().Be("user-1");
    }

    [Fact]
    public void BuildAggregate_WithJoins_JoinedTypeOwnerConstraint_AppendsConditionToOnClause_NotWhere()
    {
        // Proves BuildAggregate's join branch now threads `authz` all the way into
        // BuildFromWithJoins (Task 2's mechanism), rather than only Search doing so.
        var registry = BuildRegistry(AuthorSchema(), ArticleSchema());
        var joins = new List<JoinSpec>
        {
            new() { LeftType = "Author", RightType = "Article", LeftField = "Id", RightField = "Id", Kind = JoinKind.Inner }
        };
        var spec = new AggregationDescriptor("by_title", AggregationKind.Terms, "Article.Title");
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Article"] = new(AllowedFields: null, OwnerColumn: "OwnerId", OwnerValue: "user-1")
        };

        var (sql, param) = StarRocksQueryBuilder.BuildAggregate(
            "authors", AuthorSchema(), null, spec, joins: joins, registry: registry, authz: authz);

        sql.Should().Contain(
            "FROM `authors` INNER JOIN `articles` ON `authors`.`Id` = `articles`.`Id` " +
            "AND `articles`.`OwnerId` = @__owner1");
        sql.Should().NotContain("WHERE");
        var lookup = (SqlMapper.IParameterLookup)param;
        lookup["__owner1"].Should().Be("user-1");
    }

    // ── BuildAggregate — field reject-on-reference ─────────────────────────────

    [Fact]
    public void BuildAggregate_RestrictedField_ThrowsTranslationException()
    {
        var spec = new AggregationDescriptor("by_secret", AggregationKind.Terms, "Bio");
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Author"] = new(AllowedFields: new HashSet<string> { "Id", "Name" }, OwnerColumn: null, OwnerValue: null)
        };

        var act = () => StarRocksQueryBuilder.BuildAggregate("authors", AuthorSchema(), null, spec, authz: authz);

        act.Should().Throw<StarRocksQueryTranslationException>().WithMessage("*Bio*");
    }

    [Fact]
    public void BuildAggregate_AllowedField_DoesNotThrow()
    {
        var spec = new AggregationDescriptor("by_name", AggregationKind.Terms, "Name");
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Author"] = new(AllowedFields: new HashSet<string> { "Id", "Name" }, OwnerColumn: null, OwnerValue: null)
        };

        var act = () => StarRocksQueryBuilder.BuildAggregate("authors", AuthorSchema(), null, spec, authz: authz);

        act.Should().NotThrow();
    }

    [Fact]
    public void BuildAggregate_RestrictedGroupByField_ThrowsTranslationException()
    {
        var spec = new AggregationDescriptor(
            "by_name_bio", AggregationKind.Terms, "Name", GroupByFields: ["Name", "Bio"]);
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Author"] = new(AllowedFields: new HashSet<string> { "Id", "Name" }, OwnerColumn: null, OwnerValue: null)
        };

        var act = () => StarRocksQueryBuilder.BuildAggregate("authors", AuthorSchema(), null, spec, authz: authz);

        act.Should().Throw<StarRocksQueryTranslationException>().WithMessage("*Bio*");
    }

    [Fact]
    public void BuildAggregate_RestrictedField_ViaJoinedType_ThrowsTranslationException()
    {
        var registry = BuildRegistry(AuthorSchema(), ArticleSchema());
        var joins = new List<JoinSpec>
        {
            new() { LeftType = "Author", RightType = "Article", LeftField = "Id", RightField = "Id", Kind = JoinKind.Inner }
        };
        var spec = new AggregationDescriptor("by_body", AggregationKind.Terms, "Article.Body");
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Article"] = new(AllowedFields: new HashSet<string> { "Id", "Title" }, OwnerColumn: null, OwnerValue: null)
        };

        var act = () => StarRocksQueryBuilder.BuildAggregate(
            "authors", AuthorSchema(), null, spec, joins: joins, registry: registry, authz: authz);

        var thrown = act.Should().Throw<StarRocksQueryTranslationException>().Which;
        thrown.Message.Should().Contain("Body");
        thrown.Message.Should().Contain("Article");
    }

    [Fact]
    public void BuildAggregate_RestrictedField_ViaExpression_ThrowsTranslationException()
    {
        // Proves the reject-on-reference check tokenizes spec.Expression the same way
        // StarRocksPipelineBuilder's Derive validation does, rather than only checking
        // spec.Field — closing off the bypass where a caller routes a disallowed column
        // through Expression instead of Field.
        var spec = new AggregationDescriptor(
            "revenue", AggregationKind.Sum, "Rating", Expression: "Bio * 2");
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Author"] = new(AllowedFields: new HashSet<string> { "Id", "Name", "Rating" }, OwnerColumn: null, OwnerValue: null)
        };

        var act = () => StarRocksQueryBuilder.BuildAggregate("authors", AuthorSchema(), null, spec, authz: authz);

        act.Should().Throw<StarRocksQueryTranslationException>().WithMessage("*Bio*");
    }

    [Fact]
    public void BuildAggregate_AllowedExpression_DoesNotThrow()
    {
        var spec = new AggregationDescriptor(
            "revenue", AggregationKind.Sum, "Rating", Expression: "Rating * 2");
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Author"] = new(AllowedFields: new HashSet<string> { "Id", "Name", "Rating" }, OwnerColumn: null, OwnerValue: null)
        };

        var act = () => StarRocksQueryBuilder.BuildAggregate("authors", AuthorSchema(), null, spec, authz: authz);

        act.Should().NotThrow();
    }

    [Fact]
    public void BuildAggregate_ExpressionReferencesUnknownColumn_ThrowsTranslationException()
    {
        // No authz restriction at all — this must still be rejected because the identifier
        // doesn't resolve to any known column (nor is it a whitelisted SQL keyword/function).
        var spec = new AggregationDescriptor(
            "revenue", AggregationKind.Sum, "Rating", Expression: "NoSuchColumn * 2");

        var act = () => StarRocksQueryBuilder.BuildAggregate("authors", AuthorSchema(), null, spec);

        act.Should().Throw<StarRocksQueryTranslationException>().WithMessage("*NoSuchColumn*");
    }

    [Fact]
    public void BuildAggregate_ExpressionUsesWhitelistedFunction_DoesNotThrow()
    {
        var spec = new AggregationDescriptor(
            "revenue", AggregationKind.Sum, "Rating", Expression: "COALESCE(Rating, 0)");
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Author"] = new(AllowedFields: new HashSet<string> { "Id", "Name", "Rating" }, OwnerColumn: null, OwnerValue: null)
        };

        var act = () => StarRocksQueryBuilder.BuildAggregate("authors", AuthorSchema(), null, spec, authz: authz);

        act.Should().NotThrow();
    }

    [Fact]
    public void BuildAggregate_NoAuthz_DoesNotEnforceFieldRestrictions()
    {
        var spec = new AggregationDescriptor("by_bio", AggregationKind.Terms, "Bio");

        var act = () => StarRocksQueryBuilder.BuildAggregate("authors", AuthorSchema(), null, spec);

        act.Should().NotThrow();
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
        act.Should().Throw<StarRocksQueryTranslationException>()
            .Where(e => e.Message.Contains("VECTOR_SIMILAR"));
    }

    // ── BuildSearch — explicit SELECT columns ─────────────────────────────────────

    [Fact]
    public void BuildSearch_NoFields_SelectsAllColumnsExplicitly()
    {
        var schema = ArticleWithProjectionSchema();

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
        var schema = ArticleWithProjectionSchema();

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
        var schema = ArticleWithProjectionSchema();

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
        var schema = ArticleWithProjectionSchema();

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
        var registry = BuildRegistry(AuthorSchema(), ArticleSchema());

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
        var registry = BuildRegistry(AuthorSchema(), ArticleSchema());

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

    // ── BuildSearch — authorization (row ownership) ────────────────────────────

    [Fact]
    public void BuildSearch_NoJoins_OwnerConstraint_WrapsExistingWhereWithOwnerPredicate()
    {
        var query = new SearchQuery();
        query.Clauses.Add(new SearchClause
        {
            Property = "Name", Operator = SearchOperator.Equals,
            Value = new SearchValue { StringVal = "Alice" }, ClauseType = SearchClauseType.Filter
        });

        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Author"] = new(AllowedFields: null, OwnerColumn: "OwnerId", OwnerValue: "user-1")
        };

        var (sql, param) = StarRocksQueryBuilder.BuildSearch(
            "authors", AuthorSchema(), query, 0, 10, authz: authz);

        sql.Should().Contain("WHERE (`Name` = @p0) AND `OwnerId` = @__ownerVal");
        var lookup = (SqlMapper.IParameterLookup)param;
        lookup["__ownerVal"].Should().Be("user-1");
    }

    [Fact]
    public void BuildSearch_NoJoins_OwnerConstraint_NoExistingWhere_UsesOwnerPredicateAlone()
    {
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Author"] = new(AllowedFields: null, OwnerColumn: "OwnerId", OwnerValue: "user-1")
        };

        var (sql, param) = StarRocksQueryBuilder.BuildSearch(
            "authors", AuthorSchema(), null, 0, 10, authz: authz);

        sql.Should().Contain("WHERE `OwnerId` = @__ownerVal");
        var lookup = (SqlMapper.IParameterLookup)param;
        lookup["__ownerVal"].Should().Be("user-1");
    }

    [Fact]
    public void BuildSearch_NoJoins_NoOwnerColumn_DoesNotAddOwnerPredicate()
    {
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Author"] = new(AllowedFields: null, OwnerColumn: null, OwnerValue: null)
        };

        var (sql, _) = StarRocksQueryBuilder.BuildSearch(
            "authors", AuthorSchema(), null, 0, 10, authz: authz);

        sql.Should().NotContain("@__ownerVal");
        sql.Should().NotContain("WHERE");
    }

    [Fact]
    public void BuildSearch_WithJoins_OwnerConstraint_QualifiesOwnerColumnWithPrimaryAlias()
    {
        var registry = BuildRegistry(AuthorSchema(), ArticleSchema());
        var joins = new List<JoinSpec>
        {
            new() { LeftType = "Author", RightType = "Article", LeftField = "Id", RightField = "Id", Kind = JoinKind.Inner }
        };

        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Author"] = new(AllowedFields: null, OwnerColumn: "OwnerId", OwnerValue: "user-1")
        };

        var (sql, param) = StarRocksQueryBuilder.BuildSearch(
            "authors", AuthorSchema(), null, 0, 10, joins: joins, registry: registry, authz: authz);

        sql.Should().Contain("WHERE `authors`.`OwnerId` = @__ownerVal");
        var lookup = (SqlMapper.IParameterLookup)param;
        lookup["__ownerVal"].Should().Be("user-1");
    }

    // ── BuildFromWithJoins ─────────────────────────────────────────────────────

    private static Func<string, StarRocksQuerySchema?> BuildRegistry(params StarRocksQuerySchema[] schemas)
    {
        var map = schemas.ToDictionary(s => s.TypeName, StringComparer.OrdinalIgnoreCase);
        return typeName => map.GetValueOrDefault(typeName);
    }

    [Fact]
    public void BuildFromWithJoins_InnerJoin_ProducesJoinClause()
    {
        var registry = BuildRegistry(AuthorSchema(), ArticleSchema());

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

        var from = StarRocksQueryBuilder.BuildFromWithJoins(AuthorSchema(), joins, registry, new DynamicParameters(), out var tableMap);

        from.Should().Be("FROM `authors` INNER JOIN `articles` ON `authors`.`Id` = `articles`.`Id`");
        tableMap.Should().ContainKey("Author");
        tableMap.Should().ContainKey("Article");
        tableMap["Author"].TableName.Should().Be("authors");
        tableMap["Article"].TableName.Should().Be("articles");
    }

    [Fact]
    public void BuildFromWithJoins_LeftJoin_ProducesLeftJoinClause()
    {
        var registry = BuildRegistry(AuthorSchema(), ArticleSchema());

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

        var from = StarRocksQueryBuilder.BuildFromWithJoins(AuthorSchema(), joins, registry, new DynamicParameters(), out _);

        from.Should().Contain("LEFT JOIN `articles`");
        from.Should().NotContain("INNER JOIN");
    }

    [Fact]
    public void BuildFromWithJoins_RightJoin_ProducesRightJoinClause()
    {
        var registry = BuildRegistry(AuthorSchema(), ArticleSchema());

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

        var from = StarRocksQueryBuilder.BuildFromWithJoins(AuthorSchema(), joins, registry, new DynamicParameters(), out _);

        from.Should().Contain("RIGHT JOIN `articles`");
        from.Should().NotContain("INNER JOIN");
    }

    [Fact]
    public void BuildFromWithJoins_FullJoin_ProducesJoinClause()
    {
        var registry = BuildRegistry(AuthorSchema(), ArticleSchema());

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

        var from = StarRocksQueryBuilder.BuildFromWithJoins(AuthorSchema(), joins, registry, new DynamicParameters(), out var tableMap);

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

        var act = () => StarRocksQueryBuilder.BuildFromWithJoins(AuthorSchema(), joins, registry, new DynamicParameters(), out _);

        act.Should().Throw<StarRocksQueryTranslationException>()
            .WithMessage("*NoSuchType*");
    }

    [Fact]
    public void BuildFromWithJoins_NoJoins_StillPopulatesTableMapWithPrimaryTable()
    {
        var registry = BuildRegistry(AuthorSchema());

        var from = StarRocksQueryBuilder.BuildFromWithJoins(AuthorSchema(), [], registry, new DynamicParameters(), out var tableMap);

        from.Should().Be("FROM `authors`");
        tableMap.Should().ContainKey("Author");
        tableMap["Author"].TableName.Should().Be("authors");
        tableMap["Author"].Alias.Should().Be("authors");
    }

    [Fact]
    public void ResolveColumn_JoinContext_DotNotationResolves()
    {
        var registry = BuildRegistry(AuthorSchema(), ArticleSchema());

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

        StarRocksQueryBuilder.BuildFromWithJoins(AuthorSchema(), joins, registry, new DynamicParameters(), out var tableMap);

        var col = StarRocksQueryBuilder.ResolveColumn(tableMap, "Article.Title");

        col.Should().Be("articles.Title");
    }

    // ── BuildFromWithJoins — joined-type authorization ─────────────────────────

    [Fact]
    public void BuildFromWithJoins_JoinedTypeOwnerConstraint_AppendsConditionToOnClause_NotWhere()
    {
        var registry = BuildRegistry(AuthorSchema(), ArticleSchema());
        var joins = new List<JoinSpec>
        {
            new() { LeftType = "Author", RightType = "Article", LeftField = "Id", RightField = "Id", Kind = JoinKind.Inner }
        };
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Article"] = new(AllowedFields: null, OwnerColumn: "OwnerId", OwnerValue: "user-1")
        };
        var param = new DynamicParameters();

        var from = StarRocksQueryBuilder.BuildFromWithJoins(AuthorSchema(), joins, registry, param, out _, authz);

        // The condition must live inside the ON clause of the Article JOIN, never spliced onto
        // an outer WHERE — see the method's <remarks> for why that placement matters for
        // non-INNER joins.
        from.Should().Be(
            "FROM `authors` INNER JOIN `articles` ON `authors`.`Id` = `articles`.`Id` " +
            "AND `articles`.`OwnerId` = @__owner1");
        from.Should().NotContain("WHERE");

        var lookup = (SqlMapper.IParameterLookup)param;
        lookup["__owner1"].Should().Be("user-1");
    }

    [Fact]
    public void BuildFromWithJoins_LeftJoin_JoinedTypeOwnerConstraint_ConditionInOnClause_PreservesLeftJoinKind()
    {
        var registry = BuildRegistry(AuthorSchema(), ArticleSchema());
        var joins = new List<JoinSpec>
        {
            new() { LeftType = "Author", RightType = "Article", LeftField = "Id", RightField = "Id", Kind = JoinKind.Left }
        };
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Article"] = new(AllowedFields: null, OwnerColumn: "OwnerId", OwnerValue: "user-1")
        };
        var param = new DynamicParameters();

        var from = StarRocksQueryBuilder.BuildFromWithJoins(AuthorSchema(), joins, registry, param, out _, authz);

        // Putting the owner check in the ON clause (rather than WHERE) is what keeps this a real
        // LEFT JOIN: a non-owned/non-matching Article row surfaces as a row with NULLs on the
        // Article side, rather than being dropped as WHERE-clause placement would cause (which
        // would silently degrade the LEFT JOIN to INNER JOIN behavior).
        from.Should().Be(
            "FROM `authors` LEFT JOIN `articles` ON `authors`.`Id` = `articles`.`Id` " +
            "AND `articles`.`OwnerId` = @__owner1");
        from.Should().Contain("LEFT JOIN");
        from.Should().NotContain("WHERE");
    }

    [Fact]
    public void BuildFromWithJoins_NoConstraintForJoinedType_OmitsOwnerCondition()
    {
        var registry = BuildRegistry(AuthorSchema(), ArticleSchema());
        var joins = new List<JoinSpec>
        {
            new() { LeftType = "Author", RightType = "Article", LeftField = "Id", RightField = "Id", Kind = JoinKind.Inner }
        };
        // authz has a constraint for a type that isn't part of this join — must not affect it.
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Author"] = new(AllowedFields: null, OwnerColumn: "OwnerId", OwnerValue: "user-1")
        };
        var param = new DynamicParameters();

        var from = StarRocksQueryBuilder.BuildFromWithJoins(AuthorSchema(), joins, registry, param, out _, authz);

        from.Should().Be("FROM `authors` INNER JOIN `articles` ON `authors`.`Id` = `articles`.`Id`");
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
        var registry = BuildRegistry(AuthorSchema(), ArticleSchema());

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

        act.Should().Throw<StarRocksQueryTranslationException>()
            .WithMessage("*bad_sum*");
    }

    [Fact]
    public void BuildGroupBy_WithJoin_WhereOnJoinedTableColumn_QuotesAliasAndFieldSeparately()
    {
        var registry = BuildRegistry(AuthorSchema(), ArticleSchema());

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

        act.Should().Throw<StarRocksQueryTranslationException>()
            .Where(e => e.Message.Contains("VECTOR_SIMILAR")
                     && e.Message.Contains("SearchSimilar"));
    }

    [Fact]
    public void BuildHaving_VectorSimilarClause_ThrowsInvalidArgument()
    {
        var param = new DynamicParameters();
        var act = () => StarRocksQueryBuilder.BuildHaving(
            [VectorClause()], SearchLogic.And, param);

        act.Should().Throw<StarRocksQueryTranslationException>()
            .Where(e => e.Message.Contains("VECTOR_SIMILAR")
                     && e.Message.Contains("SearchSimilar"));
    }
}
