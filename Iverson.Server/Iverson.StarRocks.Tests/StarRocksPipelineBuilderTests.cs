using Dapper;
using FluentAssertions;
using Iverson.Client.Contracts;
using Xunit;

namespace Iverson.StarRocks.Tests;

public class StarRocksPipelineBuilderTests
{
    private static StarRocksQuerySchema ArticleSchema() => new(
        "Article", "articles", "Id",
        ["Title", "Category", "WordCount", "IsPublished", "PublishedAt", "AuthorId"]);

    private static Func<string, StarRocksQuerySchema?> BuildRegistry(params StarRocksQuerySchema[] schemas)
    {
        var map = schemas.ToDictionary(s => s.TypeName, StringComparer.OrdinalIgnoreCase);
        return typeName => map.GetValueOrDefault(typeName);
    }

    private static Func<string, StarRocksQuerySchema?> EmptyRegistry() => BuildRegistry();

    private static StarRocksQuerySchema AuthorSchemaLocal() => new(
        "Author", "authors", "Id", ["Name"]);

    private static Func<string, StarRocksQuerySchema?> RegistryWithAuthor() =>
        BuildRegistry(AuthorSchemaLocal());

    private static PipelineRequest Request(params PipelineStep[] steps)
    {
        var r = new PipelineRequest { TypeName = "Article" };
        r.Steps.AddRange(steps);
        return r;
    }

    private static void AssertInvalid(Action act, string messagePart)
    {
        var ex = Assert.Throws<StarRocksQueryTranslationException>(act);
        ex.Message.Should().Contain(messagePart);
    }

    // ── Column tracking ────────────────────────────────────────────────────────

    [Fact]
    public void Track_BaseStep_ExposesSchemaColumns()
    {
        var cols = StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(), EmptyRegistry());

        cols.Should().HaveCount(1);
        cols[0].Name.Should().Be("base");
        cols[0].Columns.Keys.Should().Contain(["Id", "Title", "WordCount"]);
    }

    [Fact]
    public void Track_WindowStep_AddsAliasToInputColumns()
    {
        var step = new PipelineStep { Name = "ranked" };
        step.Windows.Add(new WindowFunction
        {
            Alias = "rn", Kind = WindowFunctionKind.RowNumber,
            PartitionBy = "AuthorId", OrderBy = "PublishedAt", Descending = true
        });

        var cols = StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry());

        cols[1].Name.Should().Be("ranked");
        cols[1].Columns.Keys.Should().Contain("rn");
        cols[1].Columns.Keys.Should().Contain("Title");   // input columns flow through
    }

    [Fact]
    public void Track_AggregateStep_ReplacesColumnsWithKeysAndAliases()
    {
        var step = new PipelineStep { Name = "by_author" };
        step.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        step.Metrics.Add(new MetricSpec { Name = "articles", Type = AggregationType.Count });

        var cols = StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry());

        cols[1].Columns.Keys.Should().BeEquivalentTo(["AuthorId", "articles"]);
    }

    [Fact]
    public void Track_DateTruncKey_ProducesSuffixedAlias()
    {
        var step = new PipelineStep { Name = "monthly" };
        step.GroupBy.Add(new GroupKey { Field = "PublishedAt", DateTrunc = DateTrunc.Month });
        step.Metrics.Add(new MetricSpec { Name = "n", Type = AggregationType.Count });

        var cols = StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry());

        cols[1].Columns.Keys.Should().Contain("PublishedAt_month");
    }

    [Fact]
    public void Track_ReadsSelectsEarlierStep()
    {
        var agg = new PipelineStep { Name = "agg" };
        agg.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        agg.Metrics.Add(new MetricSpec { Name = "n", Type = AggregationType.Count });

        var fromBase = new PipelineStep { Name = "raw", Reads = "base" };
        fromBase.Derive.Add(new DeriveColumn { Alias = "wc2", Expr = "WordCount * 2" });

        var cols = StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(agg, fromBase), EmptyRegistry());

        // "raw" read base, not "agg" — so it still has Title plus its derive alias
        cols[2].Columns.Keys.Should().Contain(["Title", "wc2"]);
    }

    // ── Validation rules ──────────────────────────────────────────────────────

    [Fact]
    public void Validate_DuplicateStepName_Throws() =>
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(),
            Request(new PipelineStep { Name = "x" }, new PipelineStep { Name = "X" }),
            EmptyRegistry()), "x");

    [Fact]
    public void Validate_InvalidIdentifierStepName_Throws() =>
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(new PipelineStep { Name = "1bad name" }),
            EmptyRegistry()), "1bad name");

    [Fact]
    public void Validate_StepNamedBase_Throws() =>
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(new PipelineStep { Name = "base" }),
            EmptyRegistry()), "base");

    [Fact]
    public void Validate_ForwardReads_Throws()
    {
        var s1 = new PipelineStep { Name = "a", Reads = "b" };
        var s2 = new PipelineStep { Name = "b" };
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(s1, s2), EmptyRegistry()), "b");
    }

    [Fact]
    public void Validate_WindowsAndGroupByTogether_Throws()
    {
        var step = new PipelineStep { Name = "bad" };
        step.Windows.Add(new WindowFunction { Alias = "rn", Kind = WindowFunctionKind.RowNumber, OrderBy = "Id" });
        step.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry()), "bad");
    }

    [Fact]
    public void Validate_UnknownWhereColumn_Throws()
    {
        var step = new PipelineStep { Name = "f" };
        step.Where.Add(new SearchClause
        {
            Property = "Nope", Operator = SearchOperator.Equals,
            Value = new SearchValue { NumberVal = 1 }, ClauseType = SearchClauseType.Filter
        });
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry()), "Nope");
    }

    [Fact]
    public void Validate_HavingUnknownAlias_Throws()
    {
        var step = new PipelineStep { Name = "agg" };
        step.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        step.Metrics.Add(new MetricSpec { Name = "n", Type = AggregationType.Count });
        step.Having.Add(new SearchClause
        {
            Property = "misspelled", Operator = SearchOperator.GreaterThan,
            Value = new SearchValue { NumberVal = 1 }, ClauseType = SearchClauseType.Filter
        });
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry()), "misspelled");
    }

    [Fact]
    public void Validate_DuplicateAliasWithinStep_Throws()
    {
        var step = new PipelineStep { Name = "w" };
        step.Windows.Add(new WindowFunction { Alias = "x", Kind = WindowFunctionKind.RowNumber, OrderBy = "Id" });
        step.Derive.Add(new DeriveColumn { Alias = "x", Expr = "WordCount + 1" });
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry()), "x");
    }

    [Fact]
    public void Validate_WindowWithoutOrderBy_Throws()
    {
        var step = new PipelineStep { Name = "w" };
        step.Windows.Add(new WindowFunction { Alias = "rn", Kind = WindowFunctionKind.RowNumber });
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry()), "rn");
    }

    [Fact]
    public void Validate_RunningSumWithoutField_Throws()
    {
        var step = new PipelineStep { Name = "w" };
        step.Windows.Add(new WindowFunction
            { Alias = "cume", Kind = WindowFunctionKind.RunningSum, OrderBy = "PublishedAt" });
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry()), "cume");
    }

    [Fact]
    public void Validate_DeriveWithUnknownIdentifier_Throws()
    {
        var step = new PipelineStep { Name = "d" };
        step.Derive.Add(new DeriveColumn { Alias = "r", Expr = "Bogus / WordCount" });
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry()), "Bogus");
    }

    [Fact]
    public void Validate_DeriveWithSemicolon_Throws()
    {
        var step = new PipelineStep { Name = "d" };
        step.Derive.Add(new DeriveColumn { Alias = "r", Expr = "WordCount; DROP TABLE x" });
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry()), "r");
    }

    [Fact]
    public void Validate_DeriveAllowsWhitelistedWindowExpr()
    {
        var agg = new PipelineStep { Name = "agg" };
        agg.GroupBy.Add(new GroupKey { Field = "Category" });
        agg.Metrics.Add(new MetricSpec { Name = "n", Type = AggregationType.Count });

        var d = new PipelineStep { Name = "pct" };
        d.Derive.Add(new DeriveColumn { Alias = "pct_of_total", Expr = "100.0 * n / SUM(n) OVER ()" });

        var cols = StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(agg, d), EmptyRegistry());

        cols[2].Columns.Keys.Should().Contain("pct_of_total");
    }

    [Fact]
    public void Validate_JoinsWithoutSelect_Throws()
    {
        var step = new PipelineStep { Name = "j" };
        step.Joins.Add(new PipelineJoin { Source = "Author", Kind = JoinKind.Inner });
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry()), "select");
    }

    [Fact]
    public void Validate_SelectOnAggregateStep_Throws()
    {
        var step = new PipelineStep { Name = "agg" };
        step.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        step.Metrics.Add(new MetricSpec { Name = "n", Type = AggregationType.Count });
        step.Select.Add(new SelectItem { All = true });
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry()), "agg");
    }

    [Fact]
    public void Validate_SelectAliasWithBacktick_Throws()
    {
        var step = new PipelineStep { Name = "s" };
        step.Select.Add(new SelectItem { Column = "AuthorId", Alias = "bad`alias" });
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry()), "bad`alias");
    }

    [Fact]
    public void Validate_SelectAliasStartingWithDigit_Throws()
    {
        var step = new PipelineStep { Name = "s" };
        step.Select.Add(new SelectItem { Column = "AuthorId", Alias = "1bad" });
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry()), "1bad");
    }

    [Fact]
    public void Validate_SelectAliasValid_AppearsInTrackedOutput()
    {
        var step = new PipelineStep { Name = "s" };
        step.Select.Add(new SelectItem { Column = "AuthorId", Alias = "author_name" });

        var cols = StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry());

        cols[1].Columns.Keys.Should().Contain("author_name");
    }

    // ── SQL emission ──────────────────────────────────────────────────────────

    private static string NormalizeWs(string sql) =>
        System.Text.RegularExpressions.Regex.Replace(sql, @"\s+", " ").Trim();

    [Fact]
    public void Build_EmptyPipeline_SelectsFromBaseWithDefaultLimit()
    {
        var (sql, _, _) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), Request(), EmptyRegistry());

        NormalizeWs(sql).Should().Be(
            "WITH `base` AS (SELECT * FROM `articles`) SELECT * FROM `base` LIMIT 10000");
    }

    [Fact]
    public void Build_BaseWhere_UsesS0Prefix()
    {
        var request = Request();
        request.BaseWhere.Add(new SearchClause
        {
            Property = "IsPublished", Operator = SearchOperator.Equals,
            Value = new SearchValue { BoolVal = true }, ClauseType = SearchClauseType.Filter
        });

        var (sql, param, _) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), request, EmptyRegistry());

        sql.Should().Contain("WHERE `IsPublished` = @s0_p0");
        param.Get<bool>("s0_p0").Should().BeTrue();
    }

    [Fact]
    public void Build_WindowThenFilterOnAlias_TopNPerGroup()
    {
        var ranked = new PipelineStep { Name = "ranked" };
        ranked.Windows.Add(new WindowFunction
        {
            Alias = "rn", Kind = WindowFunctionKind.RowNumber,
            PartitionBy = "AuthorId", OrderBy = "PublishedAt", Descending = true
        });

        var top = new PipelineStep { Name = "top5" };
        top.Where.Add(new SearchClause
        {
            Property = "rn", Operator = SearchOperator.LessThanOrEquals,
            Value = new SearchValue { NumberVal = 5 }, ClauseType = SearchClauseType.Filter
        });

        var (sql, param, _) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), Request(ranked, top), EmptyRegistry());

        var n = NormalizeWs(sql);
        n.Should().Contain(
            "`ranked` AS (SELECT *, ROW_NUMBER() OVER (PARTITION BY `AuthorId` ORDER BY `PublishedAt` DESC) AS `rn` FROM `base`)");
        n.Should().Contain("`top5` AS (SELECT * FROM `ranked` WHERE `rn` <= @s2_p0)");
        param.Get<double>("s2_p0").Should().Be(5);
    }

    [Fact]
    public void Build_AggregateStep_EmitsGroupByAndHaving()
    {
        var step = new PipelineStep { Name = "by_author" };
        step.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        step.Metrics.Add(new MetricSpec { Name = "articles", Type = AggregationType.Count });
        step.Metrics.Add(new MetricSpec { Name = "avg_wc", Type = AggregationType.Avg, Field = "WordCount" });
        step.Having.Add(new SearchClause
        {
            Property = "articles", Operator = SearchOperator.GreaterThan,
            Value = new SearchValue { NumberVal = 5 }, ClauseType = SearchClauseType.Filter
        });

        var (sql, param, _) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), Request(step), EmptyRegistry());

        var n = NormalizeWs(sql);
        n.Should().Contain(
            "`by_author` AS (SELECT `AuthorId`, COUNT(*) AS `articles`, AVG(`WordCount`) AS `avg_wc` " +
            "FROM `base` GROUP BY `AuthorId` HAVING `articles` > @s1_h0)");
        param.Get<double>("s1_h0").Should().Be(5);
    }

    [Fact]
    public void Build_DateTruncKey_EmitsDateTruncWithAlias()
    {
        var step = new PipelineStep { Name = "monthly" };
        step.GroupBy.Add(new GroupKey { Field = "PublishedAt", DateTrunc = DateTrunc.Month });
        step.Metrics.Add(new MetricSpec { Name = "n", Type = AggregationType.Count });

        var (sql, _, _) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), Request(step), EmptyRegistry());

        var n = NormalizeWs(sql);
        n.Should().Contain("DATE_TRUNC('month', `PublishedAt`) AS `PublishedAt_month`");
        n.Should().Contain("GROUP BY `PublishedAt_month`");
    }

    [Fact]
    public void Build_RunningSumAndDerive_EmitOverAndExpression()
    {
        var agg = new PipelineStep { Name = "monthly" };
        agg.GroupBy.Add(new GroupKey { Field = "PublishedAt", DateTrunc = DateTrunc.Month });
        agg.Metrics.Add(new MetricSpec { Name = "n", Type = AggregationType.Count });

        var w = new PipelineStep { Name = "cume" };
        w.Windows.Add(new WindowFunction
        {
            Alias = "running_total", Kind = WindowFunctionKind.RunningSum,
            Field = "n", OrderBy = "PublishedAt_month"
        });

        var d = new PipelineStep { Name = "share" };
        d.Derive.Add(new DeriveColumn { Alias = "pct", Expr = "100.0 * n / SUM(n) OVER ()" });

        var (sql, _, _) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), Request(agg, w, d), EmptyRegistry());

        var n = NormalizeWs(sql);
        n.Should().Contain("SUM(`n`) OVER (ORDER BY `PublishedAt_month` ASC) AS `running_total`");
        n.Should().Contain("(100.0 * n / SUM(n) OVER ()) AS `pct`");
    }

    [Fact]
    public void Build_ReadsSkipsIntermediateStep()
    {
        var a = new PipelineStep { Name = "a" };
        a.Derive.Add(new DeriveColumn { Alias = "x", Expr = "WordCount + 1" });
        var b = new PipelineStep { Name = "b", Reads = "base" };
        b.Derive.Add(new DeriveColumn { Alias = "y", Expr = "WordCount + 2" });

        var (sql, _, _) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), Request(a, b), EmptyRegistry());

        NormalizeWs(sql).Should().Contain("`b` AS (SELECT *, (WordCount + 2) AS `y` FROM `base`)");
    }

    [Fact]
    public void Build_FinalOrderByAndLimit()
    {
        var step = new PipelineStep { Name = "agg" };
        step.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        step.Metrics.Add(new MetricSpec { Name = "n", Type = AggregationType.Count });

        var request = Request(step);
        request.OrderBy.Add(new SearchSort { Property = "n", Descending = true });
        request.Limit = 10;

        var (sql, _, _) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), request, EmptyRegistry());

        NormalizeWs(sql).Should().EndWith("SELECT * FROM `agg` ORDER BY `n` DESC LIMIT 10");
    }

    [Fact]
    public void Build_LagWindow_DefaultsOffsetToOne()
    {
        var step = new PipelineStep { Name = "lagged" };
        step.Windows.Add(new WindowFunction
        {
            Alias = "prev_wc", Kind = WindowFunctionKind.Lag,
            Field = "WordCount", OrderBy = "PublishedAt"
        });

        var (sql, _, _) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), Request(step), EmptyRegistry());

        NormalizeWs(sql).Should().Contain(
            "LAG(`WordCount`, 1) OVER (ORDER BY `PublishedAt` ASC) AS `prev_wc`");
    }

    // ── Joins ─────────────────────────────────────────────────────────────────

    private static PipelineStep JoinedStep()
    {
        var step = new PipelineStep { Name = "named" };
        var join = new PipelineJoin { Source = "Author", Kind = JoinKind.Inner };
        join.On.Add(new JoinCondition { Left = "AuthorId", Right = "Id" });
        step.Joins.Add(join);
        step.Select.Add(new SelectItem { All = true });                       // input.*
        step.Select.Add(new SelectItem { Source = "Author", Column = "Name", Alias = "author_name" });
        return step;
    }

    [Fact]
    public void Track_JoinedStep_OutputMergesSelectItems()
    {
        var cols = StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(JoinedStep()), RegistryWithAuthor());

        cols[1].Columns.Keys.Should().Contain(["Title", "author_name"]);
        cols[1].Columns.Keys.Should().NotContain("Name");   // only exposed via its alias
    }

    [Fact]
    public void Build_JoinAgainstEntityTable_EmitsAliasedJoin()
    {
        var (sql, _, _) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), Request(JoinedStep()), RegistryWithAuthor());

        var n = NormalizeWs(sql);
        n.Should().Contain(
            "`named` AS (SELECT `base`.*, `Author`.`Name` AS `author_name` FROM `base` " +
            "INNER JOIN `authors` AS `Author` ON `base`.`AuthorId` = `Author`.`Id`)");
    }

    [Fact]
    public void Build_JoinAgainstPriorCte_EmitsCteJoin()
    {
        var agg = new PipelineStep { Name = "by_author" };
        agg.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        agg.Metrics.Add(new MetricSpec { Name = "articles", Type = AggregationType.Count });

        var enriched = new PipelineStep { Name = "enriched", Reads = "base" };
        var join = new PipelineJoin { Source = "by_author", Kind = JoinKind.Left };
        join.On.Add(new JoinCondition { Left = "AuthorId", Right = "AuthorId" });
        enriched.Joins.Add(join);
        enriched.Select.Add(new SelectItem { All = true });
        enriched.Select.Add(new SelectItem { Source = "by_author", Column = "articles" });

        var (sql, _, _) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), Request(agg, enriched), EmptyRegistry());

        var n = NormalizeWs(sql);
        n.Should().Contain(
            "`enriched` AS (SELECT `base`.*, `by_author`.`articles` FROM `base` " +
            "LEFT JOIN `by_author` ON `base`.`AuthorId` = `by_author`.`AuthorId`)");
    }

    [Fact]
    public void Build_SelectAllFromJoinSource_EmitsJoinSourceWildcard()
    {
        var agg = new PipelineStep { Name = "by_author" };
        agg.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        agg.Metrics.Add(new MetricSpec { Name = "articles", Type = AggregationType.Count });

        var enriched = new PipelineStep { Name = "enriched", Reads = "base" };
        var join = new PipelineJoin { Source = "by_author", Kind = JoinKind.Inner };
        join.On.Add(new JoinCondition { Left = "AuthorId", Right = "AuthorId" });
        enriched.Joins.Add(join);
        enriched.Select.Add(new SelectItem { Source = "by_author", All = true });

        var request = new PipelineRequest { TypeName = "Article" };
        request.Steps.Add(agg);
        request.Steps.Add(enriched);

        var (sql, _, _) = StarRocksPipelineBuilder.Build(ArticleSchema(), request, EmptyRegistry());

        sql.Should().Contain("`by_author`.*");
    }

    [Fact]
    public void Build_JoinSourceNameDifferentCase_ResolvesViaOrdinalIgnoreCase()
    {
        var agg = new PipelineStep { Name = "by_author" };
        agg.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        agg.Metrics.Add(new MetricSpec { Name = "articles", Type = AggregationType.Count });

        var enriched = new PipelineStep { Name = "enriched", Reads = "base" };
        var join = new PipelineJoin { Source = "BY_AUTHOR", Kind = JoinKind.Inner }; // different case
        join.On.Add(new JoinCondition { Left = "AuthorId", Right = "AuthorId" });
        enriched.Joins.Add(join);
        enriched.Select.Add(new SelectItem { All = true });

        var request = new PipelineRequest { TypeName = "Article" };
        request.Steps.Add(agg);
        request.Steps.Add(enriched);

        var (sql, _, _) = StarRocksPipelineBuilder.Build(ArticleSchema(), request, EmptyRegistry());

        // Proves the join resolved to the correct canonically-cased source (not just that
        // *something* didn't throw) — a regression that silently dropped the join, or joined
        // to the wrong source, would not be caught by NotThrow() alone.
        sql.Should().Contain("INNER JOIN `by_author`");
    }

    [Fact]
    public void Validate_JoinOnUnknownRightColumn_Throws()
    {
        var step = JoinedStep();
        step.Joins[0].On[0].Right = "Nope";
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), RegistryWithAuthor()), "Nope");
    }

    [Fact]
    public void Validate_JoinUnknownSource_Throws()
    {
        var step = JoinedStep();
        step.Joins[0].Source = "Ghost";
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), RegistryWithAuthor()), "Ghost");
    }

    [Fact]
    public void Validate_JoinWithoutOnConditions_Throws()
    {
        var step = JoinedStep();
        step.Joins[0].On.Clear();
        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), RegistryWithAuthor()), "named");
    }

    [Fact]
    public void Validate_JoinForwardStepSource_Throws()
    {
        var joined = new PipelineStep { Name = "j" };
        var join = new PipelineJoin { Source = "later", Kind = JoinKind.Inner };
        join.On.Add(new JoinCondition { Left = "AuthorId", Right = "AuthorId" });
        joined.Joins.Add(join);
        joined.Select.Add(new SelectItem { All = true });

        var later = new PipelineStep { Name = "later" };

        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(joined, later), EmptyRegistry()), "later");
    }

    [Fact]
    public void Validate_JoinWithWindow_Throws()
    {
        var step = JoinedStep();
        step.Windows.Add(new WindowFunction
            { Alias = "rn", Kind = WindowFunctionKind.RowNumber, OrderBy = "Id" });

        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), RegistryWithAuthor()), "named");
    }

    [Fact]
    public void Validate_JoinWithDerive_Throws()
    {
        var step = JoinedStep();
        step.Derive.Add(new DeriveColumn { Alias = "wc2", Expr = "WordCount * 2" });

        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), RegistryWithAuthor()), "named");
    }

    [Fact]
    public void Build_SelectAliasNotValidIdentifier_Throws()
    {
        var step = new PipelineStep { Name = "s1" };
        step.Select.Add(new SelectItem { Column = "Title", Alias = "bad alias" });

        var request = new PipelineRequest { TypeName = "Article" };
        request.Steps.Add(step);

        var act = () => StarRocksPipelineBuilder.Build(ArticleSchema(), request, EmptyRegistry());

        act.Should().Throw<StarRocksQueryTranslationException>()
            .Where(e => e.Message == "Step 's1': select alias 'bad alias' is not a valid identifier.");
    }

    [Fact]
    public void Build_WindowAliasNotValidIdentifier_Throws()
    {
        var step = new PipelineStep { Name = "s1" };
        step.Windows.Add(new WindowFunction { Alias = "bad alias", Kind = WindowFunctionKind.RowNumber, OrderBy = "AuthorId" });

        var request = new PipelineRequest { TypeName = "Article" };
        request.Steps.Add(step);

        var act = () => StarRocksPipelineBuilder.Build(ArticleSchema(), request, EmptyRegistry());

        act.Should().Throw<StarRocksQueryTranslationException>()
            .Where(e => e.Message == "Step 's1': window alias 'bad alias' is not a valid identifier.");
    }

    [Fact]
    public void Build_MetricAliasNotValidIdentifier_Throws()
    {
        var step = new PipelineStep { Name = "s1" };
        step.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        step.Metrics.Add(new MetricSpec { Name = "bad alias", Type = AggregationType.Count });

        var request = new PipelineRequest { TypeName = "Article" };
        request.Steps.Add(step);

        var act = () => StarRocksPipelineBuilder.Build(ArticleSchema(), request, EmptyRegistry());

        act.Should().Throw<StarRocksQueryTranslationException>()
            .Where(e => e.Message == "Step 's1': metric alias 'bad alias' is not a valid identifier.");
    }

    [Theory]
    [InlineData("WordCount -- drop everything")]
    [InlineData("WordCount /* comment */ + 1")]
    public void Build_DeriveExprWithSqlCommentSequence_Throws(string expr)
    {
        var step = new PipelineStep { Name = "s1" };
        step.Derive.Add(new DeriveColumn { Alias = "d", Expr = expr });

        var request = new PipelineRequest { TypeName = "Article" };
        request.Steps.Add(step);

        var act = () => StarRocksPipelineBuilder.Build(ArticleSchema(), request, EmptyRegistry());

        act.Should().Throw<StarRocksQueryTranslationException>()
            .Where(e => e.Message.Contains("forbidden character")
                     && e.Message.Contains("SQL comment sequences"));
    }

    // ── Metric expression forbidden-character denylist (Task 9 / CSR Finding #3) ──
    // m.Expression previously only ran the TokenRx identifier allow-list check (see the
    // "Authorization — metric MetricSpec.Expression check" tests below), which inspects
    // identifier-shaped substrings only — punctuation, quotes, semicolons, and SQL comment
    // sequences pass through unchecked because they never match TokenRx in the first place.
    // These tests lock in the stricter denylist (RejectForbiddenCharacters, shared with
    // ValidateDeriveExpr and StarRocksQueryBuilder's BuildAggregate/BuildMetricExpr) applied
    // to this third call site.

    [Theory]
    [InlineData("WordCount -- drop everything")]
    [InlineData("WordCount; DROP TABLE authors")]
    [InlineData("WordCount /* comment */ + 1")]
    public void Validate_MetricExpressionWithForbiddenSequence_Throws(string expr)
    {
        var step = new PipelineStep { Name = "agg" };
        step.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        step.Metrics.Add(new MetricSpec { Name = "m", Type = AggregationType.Sum, Expression = expr });

        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry()), "forbidden character");
    }

    [Fact]
    public void Build_MetricExpressionCommentAttemptsToCloseAggregateAndStripTrailingSql_ThrowsInsteadOfProducingUnsafeSql()
    {
        // The CSR-specified regression case, adapted for Pipeline: m.Expression is spliced
        // directly into the aggregate function by EmitMetric, ahead of GROUP BY/HAVING for this
        // step and — because Pipeline compiles a *chain* of CTEs into one generated SQL string —
        // every subsequent step in the chain too. An unescaped ") -- " here would previously
        // close the aggregate call early and comment out everything after it on the generated
        // single-line SQL string. Proves the fix throws rather than producing SQL with anything
        // after the injection point silently stripped.
        var step = new PipelineStep { Name = "agg" };
        step.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        step.Metrics.Add(new MetricSpec { Name = "m", Type = AggregationType.Sum, Expression = "WordCount) -- " });

        var act = () => StarRocksPipelineBuilder.Build(ArticleSchema(), Request(step), EmptyRegistry());

        act.Should().Throw<StarRocksQueryTranslationException>()
            .Where(e => e.Message.Contains("forbidden character"));
    }

    [Fact]
    public void Build_NormalMetricExpression_StillProducesExpectedSql()
    {
        // Non-adversarial expression referencing a real input column: confirms the new denylist
        // check does not regress the legitimate use case.
        var step = new PipelineStep { Name = "agg" };
        step.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        step.Metrics.Add(new MetricSpec { Name = "wc2", Type = AggregationType.Sum, Expression = "WordCount * 2" });

        var (sql, _, _) = StarRocksPipelineBuilder.Build(ArticleSchema(), Request(step), EmptyRegistry());

        NormalizeWs(sql).Should().Contain("SUM(WordCount * 2) AS `wc2`");
    }

    // ── Authorization — column-introduction filtering (Step 1) ─────────────────

    [Fact]
    public void Track_RestrictedField_ExcludedFromBaseColumns()
    {
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Article"] = new(AllowedFields: new HashSet<string> { "Id", "Title", "AuthorId" }, OwnerColumn: null, OwnerValue: null)
        };

        var cols = StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(), EmptyRegistry(), authz);

        cols[0].Columns.Keys.Should().Contain(["Id", "Title", "AuthorId"]);
        cols[0].Columns.Keys.Should().NotContain("Category");
        cols[0].Columns.Keys.Should().NotContain("WordCount");
    }

    [Fact]
    public void Track_NoAuthz_ExposesAllColumns()
    {
        var cols = StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(), EmptyRegistry());

        cols[0].Columns.Keys.Should().Contain(["Id", "Title", "Category", "WordCount", "AuthorId"]);
    }

    [Fact]
    public void Validate_WhereReferencesDisallowedColumn_Throws()
    {
        // Category is excluded from the base step's tracked columns by ColumnsFor's Step 1
        // filtering, so a `where` clause on it fails RequireColumn's ordinary unknown-column
        // check — no separate authorization-specific WHERE check is needed.
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Article"] = new(AllowedFields: new HashSet<string> { "Id", "Title", "AuthorId" }, OwnerColumn: null, OwnerValue: null)
        };
        var step = new PipelineStep { Name = "f" };
        step.Where.Add(new SearchClause
        {
            Property = "Category", Operator = SearchOperator.Equals,
            Value = new SearchValue { StringVal = "news" }, ClauseType = SearchClauseType.Filter
        });

        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry(), authz), "Category");
    }

    [Fact]
    public void Validate_WhereReferencesAllowedColumn_DoesNotThrow()
    {
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Article"] = new(AllowedFields: new HashSet<string> { "Id", "Title", "AuthorId" }, OwnerColumn: null, OwnerValue: null)
        };
        var step = new PipelineStep { Name = "f" };
        step.Where.Add(new SearchClause
        {
            Property = "Title", Operator = SearchOperator.Equals,
            Value = new SearchValue { StringVal = "news" }, ClauseType = SearchClauseType.Filter
        });

        var act = () => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry(), authz);

        act.Should().NotThrow();
    }

    // ── Authorization — metric MetricSpec.Expression check (Step 3) ────────────

    [Fact]
    public void Validate_MetricExpressionReferencesDisallowedColumn_Throws()
    {
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Article"] = new(AllowedFields: new HashSet<string> { "Id", "AuthorId", "WordCount" }, OwnerColumn: null, OwnerValue: null)
        };
        var step = new PipelineStep { Name = "agg" };
        step.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        step.Metrics.Add(new MetricSpec { Name = "bad", Type = AggregationType.Sum, Expression = "Category" });

        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry(), authz), "Category");
    }

    [Fact]
    public void Validate_MetricExpressionReferencesAllowedColumn_DoesNotThrow()
    {
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Article"] = new(AllowedFields: new HashSet<string> { "Id", "AuthorId", "WordCount" }, OwnerColumn: null, OwnerValue: null)
        };
        var step = new PipelineStep { Name = "agg" };
        step.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        step.Metrics.Add(new MetricSpec { Name = "wc2", Type = AggregationType.Sum, Expression = "WordCount * 2" });

        var act = () => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry(), authz);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_MetricFieldDisallowed_WithAllowedExpression_StillThrows()
    {
        // Regression test mirroring Task 3's reviewer-found bypass in BuildAggregate (and Task
        // 4's BuildMetricExpr defense-in-depth): EmitMetric's own SQL-emission prefers Expression
        // over Field when both are set, so Field's resolved column never reaches the emitted SQL
        // in that case. MetricSpec has no mutual exclusivity between Field and Expression at the
        // proto level, so a caller could reference a disallowed Field and pair it with an
        // innocuous, allowed Expression to try to suppress the Field check. Field must still be
        // rejected — here it's RequireColumn against the already-Step-1-filtered input.Columns,
        // which runs unconditionally whenever Field is non-empty, independent of Expression.
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Article"] = new(AllowedFields: new HashSet<string> { "Id", "AuthorId", "WordCount" }, OwnerColumn: null, OwnerValue: null)
        };
        var step = new PipelineStep { Name = "agg" };
        step.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        step.Metrics.Add(new MetricSpec { Name = "bad", Type = AggregationType.Sum, Field = "Category", Expression = "WordCount" });

        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry(), authz), "Category");
    }

    [Fact]
    public void Validate_MetricExpressionReferencesUnknownColumn_ThrowsEvenWithoutAuthz()
    {
        // No authz restriction at all — must still be rejected because the identifier doesn't
        // resolve to any known column (nor is it a whitelisted SQL keyword/function).
        var step = new PipelineStep { Name = "agg" };
        step.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        step.Metrics.Add(new MetricSpec { Name = "bad", Type = AggregationType.Sum, Expression = "NoSuchColumn * 2" });

        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry()), "NoSuchColumn");
    }

    [Fact]
    public void Validate_MetricExpressionUsesWhitelistedFunction_DoesNotThrow()
    {
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Article"] = new(AllowedFields: new HashSet<string> { "Id", "AuthorId", "WordCount" }, OwnerColumn: null, OwnerValue: null)
        };
        var step = new PipelineStep { Name = "agg" };
        step.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        step.Metrics.Add(new MetricSpec { Name = "wc", Type = AggregationType.Sum, Expression = "COALESCE(WordCount, 0)" });

        var act = () => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry(), authz);

        act.Should().NotThrow();
    }

    // ── Authorization — "all: true" scoping (Step 2) ────────────────────────────

    [Fact]
    public void Validate_AllTrueAgainstRestrictedFreshJoinType_Throws()
    {
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Author"] = new(AllowedFields: new HashSet<string> { "Id" }, OwnerColumn: null, OwnerValue: null)
        };
        var step = new PipelineStep { Name = "named" };
        var join = new PipelineJoin { Source = "Author", Kind = JoinKind.Inner };
        join.On.Add(new JoinCondition { Left = "AuthorId", Right = "Id" });
        step.Joins.Add(join);
        step.Select.Add(new SelectItem { Source = "Author", All = true });

        AssertInvalid(() => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), RegistryWithAuthor(), authz), "Author");
    }

    [Fact]
    public void Validate_AllTrueAgainstUnrestrictedFreshJoinType_DoesNotThrow()
    {
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Author"] = new(AllowedFields: null, OwnerColumn: null, OwnerValue: null)
        };
        var step = new PipelineStep { Name = "named" };
        var join = new PipelineJoin { Source = "Author", Kind = JoinKind.Inner };
        join.On.Add(new JoinCondition { Left = "AuthorId", Right = "Id" });
        step.Joins.Add(join);
        step.Select.Add(new SelectItem { Source = "Author", All = true });

        var act = () => StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), RegistryWithAuthor(), authz);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_AllTrueAgainstPriorStep_SucceedsRegardlessOfFieldRestriction()
    {
        // "all: true" against a prior step (including "base") needs no fresh-type check at all —
        // per Step 1, that step's Columns dictionary is already restricted to allowed names, so
        // expansion is inherently safe even though the caller has a field restriction in play.
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Article"] = new(AllowedFields: new HashSet<string> { "Id", "AuthorId", "WordCount" }, OwnerColumn: null, OwnerValue: null)
        };
        var step = new PipelineStep { Name = "s" };
        step.Select.Add(new SelectItem { All = true }); // empty Source => the step's own input, i.e. "base"

        var cols = StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(step), EmptyRegistry(), authz);

        cols[1].Columns.Keys.Should().BeEquivalentTo(["Id", "AuthorId", "WordCount"]);
    }

    // ── Authorization — multi-step correctness ──────────────────────────────────

    [Fact]
    public void Track_MultiStepPipeline_ImplicitPassthroughRetainsJoinedAndDerivedColumns()
    {
        // First step: explicit multi-type select (JoinedStep — input.* plus an aliased column
        // from a fresh joined type). Second step: pure implicit passthrough (no Select at all).
        // Proves the joined/derived column ("author_name") survives an extra passthrough step
        // even with a field restriction on the joined type in play.
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Author"] = new(AllowedFields: new HashSet<string> { "Id", "Name" }, OwnerColumn: null, OwnerValue: null)
        };
        var joined = JoinedStep(); // step "named": input.* + Author.Name AS author_name
        var passthrough = new PipelineStep { Name = "final" };

        var cols = StarRocksPipelineBuilder.TrackAndValidate(
            ArticleSchema(), Request(joined, passthrough), RegistryWithAuthor(), authz);

        cols[2].Name.Should().Be("final");
        cols[2].Columns.Keys.Should().Contain(["Title", "author_name"]);
        cols[2].Columns.Keys.Should().NotContain("Name"); // only ever exposed via its alias
    }

    // ── Authorization — ownership (Step 4) ──────────────────────────────────────

    [Fact]
    public void Build_PrimaryOwnership_AppendsWrapAndAndPredicateToBaseWhere()
    {
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Article"] = new(AllowedFields: null, OwnerColumn: "OwnerId", OwnerValue: "alice")
        };

        var (sql, param, _) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), Request(), EmptyRegistry(), authz);

        sql.Should().Contain("WHERE `OwnerId` = @__ownerVal");
        var lookup = (SqlMapper.IParameterLookup)param;
        lookup["__ownerVal"].Should().Be("alice");
    }

    [Fact]
    public void Build_PrimaryOwnership_CombinesWithBaseWhereViaWrapAndAnd()
    {
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Article"] = new(AllowedFields: null, OwnerColumn: "OwnerId", OwnerValue: "alice")
        };
        var request = Request();
        request.BaseWhere.Add(new SearchClause
        {
            Property = "IsPublished", Operator = SearchOperator.Equals,
            Value = new SearchValue { BoolVal = true }, ClauseType = SearchClauseType.Filter
        });

        var (sql, _, _) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), request, EmptyRegistry(), authz);

        NormalizeWs(sql).Should().Contain("WHERE (`IsPublished` = @s0_p0) AND `OwnerId` = @__ownerVal");
    }

    [Fact]
    public void Build_JoinedTypeOwnership_AppendsOwnerConditionToJoinOn()
    {
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Author"] = new(AllowedFields: null, OwnerColumn: "OwnerId", OwnerValue: "alice")
        };

        var (sql, param, _) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), Request(JoinedStep()), RegistryWithAuthor(), authz);

        var n = NormalizeWs(sql);
        n.Should().Contain("ON `base`.`AuthorId` = `Author`.`Id` AND `Author`.`OwnerId` = @s1_j0_owner");
        var lookup = (SqlMapper.IParameterLookup)param;
        lookup["s1_j0_owner"].Should().Be("alice");
    }

    [Fact]
    public void Build_JoinAgainstPriorCte_NoOwnershipPredicateAppendedEvenWhenAuthzRestrictsThatType()
    {
        // A step-to-step join needs no new ownership predicate — that source was already
        // filtered upstream (same reasoning already established for a Pipeline step's own
        // `where`). "by_author" here is a step name, not a registered type, so it must never be
        // looked up in authz at all.
        var agg = new PipelineStep { Name = "by_author" };
        agg.GroupBy.Add(new GroupKey { Field = "AuthorId" });
        agg.Metrics.Add(new MetricSpec { Name = "articles", Type = AggregationType.Count });

        var enriched = new PipelineStep { Name = "enriched", Reads = "base" };
        var join = new PipelineJoin { Source = "by_author", Kind = JoinKind.Left };
        join.On.Add(new JoinCondition { Left = "AuthorId", Right = "AuthorId" });
        enriched.Joins.Add(join);
        enriched.Select.Add(new SelectItem { All = true });
        enriched.Select.Add(new SelectItem { Source = "by_author", Column = "articles" });

        // A populated but unrelated authz entry (a registered type named "Author" — distinct
        // from the "by_author" step name, and not otherwise involved in this pipeline at all)
        // proves the join's own resolution never mistakes a step source for a registered type
        // and looks it up in authz.
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Author"] = new(AllowedFields: null, OwnerColumn: "OwnerId", OwnerValue: "alice")
        };

        var (sql, _, _) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), Request(agg, enriched), EmptyRegistry(), authz);

        var n = NormalizeWs(sql);
        n.Should().Contain(
            "`enriched` AS (SELECT `base`.*, `by_author`.`articles` FROM `base` " +
            "LEFT JOIN `by_author` ON `base`.`AuthorId` = `by_author`.`AuthorId`)");
    }

    // ── Authorization — Layer 2 masking input (Step 5) ──────────────────────────

    [Fact]
    public void Build_LastCols_MatchesFinalStepTrackedOutput_RestrictedToAllowedFields()
    {
        var authz = new Dictionary<string, AuthorizationConstraint>
        {
            ["Article"] = new(AllowedFields: new HashSet<string> { "Id", "Title", "AuthorId" }, OwnerColumn: null, OwnerValue: null)
        };

        var (_, _, lastCols) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), Request(), EmptyRegistry(), authz);

        lastCols.Keys.Should().BeEquivalentTo(["Id", "Title", "AuthorId"]);
    }

    [Fact]
    public void Build_NoAuthz_LastColsContainsAllColumns()
    {
        var (_, _, lastCols) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), Request(), EmptyRegistry());

        lastCols.Keys.Should().Contain(["Id", "Title", "Category", "WordCount", "IsPublished", "PublishedAt", "AuthorId"]);
    }
}
