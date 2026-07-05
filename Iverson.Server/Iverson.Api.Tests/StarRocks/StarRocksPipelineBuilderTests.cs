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

namespace Iverson.Api.Tests.StarRocks;

public class StarRocksPipelineBuilderTests
{
    private static SchemaDescriptor ArticleSchema() => new()
    {
        TypeName      = "Article",
        TableName     = "articles",
        KeyColumn     = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns =
        [
            new ColumnDescriptor("Title",       "text",        false),
            new ColumnDescriptor("Category",    "text",        true),
            new ColumnDescriptor("WordCount",   "integer",     true),
            new ColumnDescriptor("IsPublished", "boolean",     true),
            new ColumnDescriptor("PublishedAt", "timestamptz", true),
            new ColumnDescriptor("AuthorId",    "uuid",        true),
        ],
        FkColumns = [], VectorFields = [], ChunkFields = [], Relations = []
    };

    private static SchemaRegistry EmptyRegistry()
    {
        var sql = Substitute.For<IPostgresRepository>();
        sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(0);
        return new SchemaRegistry(sql, NullLogger<SchemaRegistry>.Instance);
    }

    private static SchemaDescriptor AuthorSchemaLocal() => new()
    {
        TypeName      = "Author",
        TableName     = "authors",
        KeyColumn     = new ColumnDescriptor("Id", "uuid", false),
        ScalarColumns = [new ColumnDescriptor("Name", "text", false)],
        FkColumns = [], VectorFields = [], ChunkFields = [], Relations = []
    };

    private static SchemaRegistry RegistryWithAuthor()
    {
        var r = EmptyRegistry();
        r.RegisterAsync(AuthorSchemaLocal()).GetAwaiter().GetResult();
        return r;
    }

    private static PipelineRequest Request(params PipelineStep[] steps)
    {
        var r = new PipelineRequest { TypeName = "Article" };
        r.Steps.AddRange(steps);
        return r;
    }

    private static void AssertInvalid(Action act, string messagePart)
    {
        var ex = Assert.Throws<RpcException>(act);
        ex.Status.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Status.Detail.Should().Contain(messagePart);
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
        var (sql, _) = StarRocksPipelineBuilder.Build(
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

        var (sql, param) = StarRocksPipelineBuilder.Build(
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

        var (sql, param) = StarRocksPipelineBuilder.Build(
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

        var (sql, param) = StarRocksPipelineBuilder.Build(
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

        var (sql, _) = StarRocksPipelineBuilder.Build(
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

        var (sql, _) = StarRocksPipelineBuilder.Build(
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

        var (sql, _) = StarRocksPipelineBuilder.Build(
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

        var (sql, _) = StarRocksPipelineBuilder.Build(
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

        var (sql, _) = StarRocksPipelineBuilder.Build(
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
        var (sql, _) = StarRocksPipelineBuilder.Build(
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

        var (sql, _) = StarRocksPipelineBuilder.Build(
            ArticleSchema(), Request(agg, enriched), EmptyRegistry());

        var n = NormalizeWs(sql);
        n.Should().Contain(
            "`enriched` AS (SELECT `base`.*, `by_author`.`articles` FROM `base` " +
            "LEFT JOIN `by_author` ON `base`.`AuthorId` = `by_author`.`AuthorId`)");
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
}
