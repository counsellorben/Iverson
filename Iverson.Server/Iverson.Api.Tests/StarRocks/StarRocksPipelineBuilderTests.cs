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
}
