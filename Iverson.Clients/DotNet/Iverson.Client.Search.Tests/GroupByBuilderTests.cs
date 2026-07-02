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
            .Join("LineItem", "Orders", "OrderId", "OrderId", JoinKind.Left)
            .Build();

        var join = req.Joins.Should().ContainSingle().Subject;
        join.LeftType.Should().Be("LineItem");
        join.RightType.Should().Be("Orders");
        join.LeftField.Should().Be("OrderId");
        join.RightField.Should().Be("OrderId");
        join.Kind.Should().Be(JoinKind.Left);
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
}
