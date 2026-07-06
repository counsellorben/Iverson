using FluentAssertions;
using Iverson.Client.Contracts;
using Iverson.Client.Search;
using Xunit;

namespace Iverson.Client.Search.Tests;

public sealed class GroupByBuilderTests
{
    // ── Keys ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Keys_AddsGroupByFields()
    {
        var req = Query.GroupBy("LineItem")
            .Keys("OrderStatus", "ShipMode")
            .Build();

        req.Keys.Should().BeEquivalentTo(["OrderStatus", "ShipMode"], o => o.WithStrictOrdering());
    }

    // ── Metrics ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Sum_AddsMetricWithAutoAlias()
    {
        var req = Query.GroupBy("LineItem")
            .Sum("ExtendedPrice")
            .Build();

        var metric = req.Metrics.Should().ContainSingle().Subject;
        metric.Name.Should().Be("ExtendedPrice_sum");
        metric.Type.Should().Be(AggregationType.Sum);
        metric.Field.Should().Be("ExtendedPrice");
        metric.Expression.Should().BeEmpty();
    }

    [Fact]
    public void SumExpr_AddsMetricWithRawExpression()
    {
        var req = Query.GroupBy("LineItem")
            .SumExpr("ExtendedPrice * (1 - Discount)", "revenue")
            .Build();

        var metric = req.Metrics.Should().ContainSingle().Subject;
        metric.Name.Should().Be("revenue");
        metric.Type.Should().Be(AggregationType.Sum);
        metric.Expression.Should().Be("ExtendedPrice * (1 - Discount)");
        metric.Field.Should().BeEmpty();
    }

    [Fact]
    public void CountAll_ProducesEmptyFieldMetric()
    {
        var req = Query.GroupBy("LineItem")
            .CountAll()
            .Build();

        var metric = req.Metrics.Should().ContainSingle().Subject;
        metric.Name.Should().Be("count");
        metric.Type.Should().Be(AggregationType.Count);
        metric.Field.Should().BeEmpty();
        metric.Expression.Should().BeEmpty();
    }

    // ── Having ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Having_AddsHavingClause()
    {
        var req = Query.GroupBy("LineItem")
            .Sum("ExtendedPrice", "total")
            .Having("total", SearchOperator.GreaterThan, 1000)
            .Build();

        var clause = req.Having.Clauses.Should().ContainSingle().Subject;
        clause.Property.Should().Be("total");
        clause.Operator.Should().Be(SearchOperator.GreaterThan);
        clause.Value.NumberVal.Should().Be(1000);
    }

    // ── Join ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Join_AddsJoinSpec()
    {
        var req = Query.GroupBy("LineItem")
            .Join("OrderId", "Orders", "OrderId", JoinKind.Left)
            .Build();

        var join = req.Joins.Should().ContainSingle().Subject;
        join.LeftType.Should().Be("LineItem");
        join.RightType.Should().Be("Orders");
        join.LeftField.Should().Be("OrderId");
        join.RightField.Should().Be("OrderId");
        join.Kind.Should().Be(JoinKind.Left);
    }

    [Fact]
    public void Join_WithFullKind_SetsKind()
    {
        var req = Query.GroupBy("LineItem")
            .Join("OrderId", "Orders", "OrderId", JoinKind.Full)
            .Build();

        var join = req.Joins.Should().ContainSingle().Subject;
        join.Kind.Should().Be(JoinKind.Full);
    }

    [Fact]
    public void Join_WithoutExplicitKind_DefaultsToInner()
    {
        var req = Query.GroupBy("LineItem")
            .Join("OrderId", "Orders", "OrderId")
            .Build();

        var join = req.Joins.Should().ContainSingle().Subject;
        join.Kind.Should().Be(JoinKind.Inner);
    }

    // ── Build metadata ──────────────────────────────────────────────────────────

    [Fact]
    public void Build_SetsTraceId()
    {
        var req = Query.GroupBy("LineItem").Build(traceId: "trace-abc");

        req.TraceId.Should().Be("trace-abc");
    }

    // ── TPC-H Q1-style composition ────────────────────────────────────────────

    [Fact]
    public void Q1Style_AllFieldsPresent()
    {
        var req = Query.GroupBy("LineItem")
            .Where("ShipDate", SearchOperator.LessThanOrEquals, "1998-12-01")
            .Keys("ReturnFlag", "LineStatus")
            .Sum("Quantity", "sum_qty")
            .Sum("ExtendedPrice", "sum_base_price")
            .SumExpr("ExtendedPrice * (1 - Discount)", "sum_disc_price")
            .SumExpr("ExtendedPrice * (1 - Discount) * (1 + Tax)", "sum_charge")
            .Avg("Quantity", "avg_qty")
            .Avg("ExtendedPrice", "avg_price")
            .Avg("Discount", "avg_disc")
            .CountAll("count_order")
            .Sum("Discount", "sum_disc")
            .OrderBy("ReturnFlag")
            .OrderBy("LineStatus")
            .Build();

        req.Keys.Should().HaveCount(2);
        req.Metrics.Should().HaveCount(9);
        req.OrderBy.Should().HaveCount(2);
        req.Query.Clauses.Should().ContainSingle();
    }

    // ── Not / HavingLogic ───────────────────────────────────────────────────────

    [Fact]
    public void Not_AddsMustNotClause()
    {
        var request = Query.GroupBy("Article")
            .Keys("Category")
            .CountAll("n")
            .Not("Category", SearchOperator.Equals, "spam")
            .Build();

        request.Query.Clauses.Should().ContainSingle(c =>
            c.ClauseType == SearchClauseType.MustNot && c.Property == "Category");
    }

    [Fact]
    public void WithHavingLogic_Or_IsCarried()
    {
        var request = Query.GroupBy("Article")
            .Keys("Category")
            .CountAll("n")
            .Having("n", SearchOperator.GreaterThan, 5)
            .Having("n", SearchOperator.LessThan, 2)
            .WithHavingLogic(SearchLogic.Or)
            .Build();

        request.Having.Logic.Should().Be(SearchLogic.Or);
    }

    // ── Build validation ────────────────────────────────────────────────────────

    [Fact]
    public void Build_DuplicateMetricAliases_Throws()
    {
        var builder = Query.GroupBy("Article")
            .Keys("Category")
            .Sum("WordCount")
            .Sum("WordCount");
        var act = () => builder.Build();
        act.Should().Throw<InvalidOperationException>().WithMessage("*WordCount_sum*");
    }

    [Fact]
    public void Build_HavingUnknownAlias_Throws()
    {
        var builder = Query.GroupBy("Article")
            .Keys("Category")
            .CountAll("n")
            .Having("misspelled", SearchOperator.GreaterThan, 5);
        var act = () => builder.Build();
        act.Should().Throw<InvalidOperationException>().WithMessage("*misspelled*");
    }

    [Fact]
    public void Build_HavingOnKey_IsAllowed()
    {
        var act = () => Query.GroupBy("Article")
            .Keys("Category")
            .CountAll("n")
            .Having("Category", SearchOperator.Equals, "tech")
            .Build();
        act.Should().NotThrow();
    }

    [Fact]
    public void Build_OrderByUnknownAlias_Throws()
    {
        var builder = Query.GroupBy("Article")
            .Keys("Category")
            .CountAll("n")
            .OrderBy("nope");
        var act = () => builder.Build();
        act.Should().Throw<InvalidOperationException>().WithMessage("*nope*");
    }

    [Fact]
    public void Build_KeyCollidesWithMetricAlias_Throws()
    {
        var builder = Query.GroupBy("Article").Keys("total").Sum("Price", "total");
        var act = () => builder.Build();
        act.Should().Throw<InvalidOperationException>().WithMessage("*total*");
    }

    [Fact]
    public void Build_HavingReferencesMetricAlias_CaseInsensitive_IsAllowed()
    {
        var act = () => Query.GroupBy("Article")
            .Keys("Category")
            .Sum("WordCount", "Total")
            .Having("TOTAL", SearchOperator.GreaterThan, 100)
            .Build();
        act.Should().NotThrow();
    }

    [Fact]
    public void Build_OrderByReferencesKey_CaseInsensitive_IsAllowed()
    {
        var act = () => Query.GroupBy("Article")
            .Keys("Category")
            .CountAll("n")
            .OrderBy("CATEGORY")
            .Build();
        act.Should().NotThrow();
    }
}
