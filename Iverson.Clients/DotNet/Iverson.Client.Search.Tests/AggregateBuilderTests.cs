using FluentAssertions;
using Iverson.Client.Contracts;
using Iverson.Client.Search;
using Xunit;

namespace Iverson.Client.Search.Tests;

public sealed class AggregateBuilderTests
{
    // ── Simple metrics ──────────────────────────────────────────────────────────

    [Fact]
    public void Sum_AddsAggregationSpec()
    {
        var req = new AggregateBuilder("LineItem")
            .Sum("ExtendedPrice", "sum_price")
            .Build();

        var spec = req.Aggregations.Should().ContainSingle().Subject;
        spec.Name.Should().Be("sum_price");
        spec.Type.Should().Be(AggregationType.Sum);
        spec.Field.Should().Be("ExtendedPrice");
    }

    [Fact]
    public void Avg_AddsAggregationSpec()
    {
        var req = new AggregateBuilder("LineItem")
            .Avg("Quantity", "avg_qty")
            .Build();

        var spec = req.Aggregations.Should().ContainSingle().Subject;
        spec.Name.Should().Be("avg_qty");
        spec.Type.Should().Be(AggregationType.Avg);
        spec.Field.Should().Be("Quantity");
    }

    [Fact]
    public void Min_AddsAggregationSpec()
    {
        var req = new AggregateBuilder("LineItem")
            .Min("Quantity", "min_qty")
            .Build();

        var spec = req.Aggregations.Should().ContainSingle().Subject;
        spec.Name.Should().Be("min_qty");
        spec.Type.Should().Be(AggregationType.Min);
        spec.Field.Should().Be("Quantity");
    }

    [Fact]
    public void Max_AddsAggregationSpec()
    {
        var req = new AggregateBuilder("LineItem")
            .Max("Quantity", "max_qty")
            .Build();

        var spec = req.Aggregations.Should().ContainSingle().Subject;
        spec.Name.Should().Be("max_qty");
        spec.Type.Should().Be(AggregationType.Max);
        spec.Field.Should().Be("Quantity");
    }

    [Fact]
    public void Count_AddsAggregationSpec()
    {
        var req = new AggregateBuilder("LineItem")
            .Count("OrderId", "order_count")
            .Build();

        var spec = req.Aggregations.Should().ContainSingle().Subject;
        spec.Name.Should().Be("order_count");
        spec.Type.Should().Be(AggregationType.Count);
        spec.Field.Should().Be("OrderId");
    }

    [Fact]
    public void CountAll_ProducesEmptyFieldSpec()
    {
        var req = new AggregateBuilder("LineItem")
            .CountAll("total")
            .Build();

        var spec = req.Aggregations.Should().ContainSingle().Subject;
        spec.Name.Should().Be("total");
        spec.Type.Should().Be(AggregationType.Count);
        spec.Field.Should().BeEmpty();
    }

    // ── Terms ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Terms_DefaultsSizeTo10()
    {
        var req = new AggregateBuilder("Article")
            .Terms("Category", "by_category")
            .Build();

        var spec = req.Aggregations.Should().ContainSingle().Subject;
        spec.Name.Should().Be("by_category");
        spec.Type.Should().Be(AggregationType.Terms);
        spec.Field.Should().Be("Category");
        spec.Size.Should().Be(10);
    }

    [Fact]
    public void Terms_WithCustomSize()
    {
        var req = new AggregateBuilder("Article")
            .Terms("Category", "by_category", 25)
            .Build();

        var spec = req.Aggregations.Should().ContainSingle().Subject;
        spec.Size.Should().Be(25);
    }

    // ── DateHistogram ───────────────────────────────────────────────────────────

    [Fact]
    public void DateHistogram_AddsAggregationSpec()
    {
        var req = new AggregateBuilder("Article")
            .DateHistogram("PublishedAt", "by_month", "month")
            .Build();

        var spec = req.Aggregations.Should().ContainSingle().Subject;
        spec.Name.Should().Be("by_month");
        spec.Type.Should().Be(AggregationType.DateHistogram);
        spec.Field.Should().Be("PublishedAt");
        spec.CalendarInterval.Should().Be("month");
        spec.TimeZone.Should().BeEmpty();
    }

    [Fact]
    public void DateHistogram_WithTimeZone()
    {
        var req = new AggregateBuilder("Article")
            .DateHistogram("PublishedAt", "by_month", "month", "America/New_York")
            .Build();

        var spec = req.Aggregations.Should().ContainSingle().Subject;
        spec.TimeZone.Should().Be("America/New_York");
    }

    // ── Range ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Range_AddsAggregationSpecWithBuckets()
    {
        var req = new AggregateBuilder("Article")
            .Range("WordCount", "by_length",
            [
                ("short", (double?)null, (double?)500),
                ("medium", 500d, 2000d),
                ("long", 2000d, (double?)null)
            ])
            .Build();

        var spec = req.Aggregations.Should().ContainSingle().Subject;
        spec.Name.Should().Be("by_length");
        spec.Type.Should().Be(AggregationType.Range);
        spec.Field.Should().Be("WordCount");
        spec.RangeBuckets.Should().HaveCount(3);

        spec.RangeBuckets[0].Key.Should().Be("short");
        spec.RangeBuckets[0].From.Should().BeNull();
        spec.RangeBuckets[0].To.Should().Be(500);

        spec.RangeBuckets[1].Key.Should().Be("medium");
        spec.RangeBuckets[1].From.Should().Be(500);
        spec.RangeBuckets[1].To.Should().Be(2000);

        spec.RangeBuckets[2].Key.Should().Be("long");
        spec.RangeBuckets[2].From.Should().Be(2000);
        spec.RangeBuckets[2].To.Should().BeNull();
    }

    // ── Where / Not / WithLogic ─────────────────────────────────────────────────

    [Fact]
    public void Where_AddsFilterClause()
    {
        var req = new AggregateBuilder("Article")
            .Where("Category", SearchOperator.Equals, "tech")
            .CountAll("n")
            .Build();

        var clause = req.Query.Clauses.Should().ContainSingle().Subject;
        clause.Property.Should().Be("Category");
        clause.Operator.Should().Be(SearchOperator.Equals);
        clause.Value.StringVal.Should().Be("tech");
        clause.ClauseType.Should().Be(SearchClauseType.Filter);
    }

    [Fact]
    public void Not_AddsMustNotClause()
    {
        var req = new AggregateBuilder("Article")
            .Not("Category", SearchOperator.Equals, "spam")
            .CountAll("n")
            .Build();

        req.Query.Clauses.Should().ContainSingle(c =>
            c.ClauseType == SearchClauseType.MustNot && c.Property == "Category");
    }

    [Fact]
    public void WithLogic_SetsQueryLogic()
    {
        var req = new AggregateBuilder("Article")
            .Where("Category", SearchOperator.Equals, "tech")
            .Where("WordCount", SearchOperator.GreaterThan, 100)
            .WithLogic(SearchLogic.Or)
            .CountAll("n")
            .Build();

        req.Query.Logic.Should().Be(SearchLogic.Or);
    }

    // ── Having / WithHavingLogic ────────────────────────────────────────────────

    [Fact]
    public void Having_AddsHavingClause()
    {
        var req = new AggregateBuilder("Article")
            .Sum("WordCount", "total")
            .Having("metric_val", SearchOperator.GreaterThan, 1000)
            .Build();

        var clause = req.Having.Clauses.Should().ContainSingle().Subject;
        clause.Property.Should().Be("metric_val");
        clause.Operator.Should().Be(SearchOperator.GreaterThan);
        clause.Value.NumberVal.Should().Be(1000);
    }

    [Fact]
    public void WithHavingLogic_Or_IsCarried()
    {
        var req = new AggregateBuilder("Article")
            .CountAll("n")
            .Having("metric_val", SearchOperator.GreaterThan, 5)
            .Having("metric_val", SearchOperator.LessThan, 2)
            .WithHavingLogic(SearchLogic.Or)
            .Build();

        req.Having.Logic.Should().Be(SearchLogic.Or);
    }

    // ── Join ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Join_AddsJoinSpec()
    {
        var req = new AggregateBuilder("LineItem")
            .Join("OrderId", "Orders", "OrderId", JoinKind.Left)
            .CountAll("n")
            .Build();

        var join = req.Joins.Should().ContainSingle().Subject;
        join.LeftType.Should().Be("LineItem");
        join.RightType.Should().Be("Orders");
        join.LeftField.Should().Be("OrderId");
        join.RightField.Should().Be("OrderId");
        join.Kind.Should().Be(JoinKind.Left);
    }

    [Fact]
    public void Join_WithoutExplicitKind_DefaultsToInner()
    {
        var req = new AggregateBuilder("LineItem")
            .Join("OrderId", "Orders", "OrderId")
            .CountAll("n")
            .Build();

        var join = req.Joins.Should().ContainSingle().Subject;
        join.Kind.Should().Be(JoinKind.Inner);
    }

    // ── Build metadata ──────────────────────────────────────────────────────────

    [Fact]
    public void Build_SetsTraceId()
    {
        var req = new AggregateBuilder("Article").CountAll("n").Build("trace-abc");

        req.TraceId.Should().Be("trace-abc");
    }

    [Fact]
    public void Build_WithNoTraceId_DefaultsToEmpty()
    {
        var req = new AggregateBuilder("Article").CountAll("n").Build();

        req.TraceId.Should().BeEmpty();
    }

    [Fact]
    public void Build_SetsTypeName()
    {
        var req = new AggregateBuilder("Article").CountAll("n").Build();

        req.TypeName.Should().Be("Article");
    }

    // ── Build validation ────────────────────────────────────────────────────────

    [Fact]
    public void Build_DuplicateAggregationName_Throws()
    {
        var builder = new AggregateBuilder("Article")
            .Sum("WordCount", "total")
            .Avg("WordCount", "total");

        var act = () => builder.Build();
        act.Should().Throw<InvalidOperationException>().WithMessage("*total*");
    }

    [Fact]
    public void Build_DuplicateAggregationName_CaseInsensitive_Throws()
    {
        var builder = new AggregateBuilder("Article")
            .Sum("WordCount", "Total")
            .Avg("WordCount", "TOTAL");

        var act = () => builder.Build();
        act.Should().Throw<InvalidOperationException>();
    }
}
