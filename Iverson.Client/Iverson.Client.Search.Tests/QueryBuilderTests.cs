using FluentAssertions;
using Iverson.Client.Contracts;
using Iverson.Client.Search;
using Xunit;

namespace Iverson.Client.Search.Tests;

public sealed class QueryBuilderTests
{
    private sealed class Article
    {
        public string  Title      { get; set; } = "";
        public string  Author     { get; set; } = "";
        public int     PageCount  { get; set; }
        public double  Rating     { get; set; }
        public bool    Published  { get; set; }
        public DateTime PublishedAt { get; set; }
    }

    // ── Build: TypeName ───────────────────────────────────────────────────────

    [Fact]
    public void Build_SetsTypeName_FromGenericParameter()
    {
        var req = Query.For<Article>().Build();

        req.TypeName.Should().Be("Article");
    }

    // ── Build: property name extraction ───────────────────────────────────────

    [Fact]
    public void Where_UsesPropertyName_AsClauseProperty()
    {
        var req = Query.For<Article>()
            .Where(x => x.Title, SearchOperator.Contains, "test")
            .Build();

        req.Query.Clauses.Should().ContainSingle()
            .Which.Property.Should().Be("Title");
    }

    [Fact]
    public void Where_ThrowsArgumentException_WhenExpressionIsNotPropertyAccess()
    {
        var act = () => Query.For<Article>()
            .Where(x => x.Title.ToUpper(), SearchOperator.Contains, "test")
            .Build();

        act.Should().Throw<ArgumentException>();
    }

    // ── Build: clause types ───────────────────────────────────────────────────

    [Fact]
    public void Where_ProducesFilterClause()
    {
        var req = Query.For<Article>()
            .Where(x => x.Title, SearchOperator.Contains, "x")
            .Build();

        req.Query.Clauses.Single().ClauseType.Should().Be(SearchClauseType.Filter);
    }

    [Fact]
    public void And_ProducesMustClause()
    {
        var req = Query.For<Article>()
            .And(x => x.Title, SearchOperator.Contains, "x")
            .Build();

        req.Query.Clauses.Single().ClauseType.Should().Be(SearchClauseType.Must);
    }

    [Fact]
    public void Or_ProducesShouldClause()
    {
        var req = Query.For<Article>()
            .Or(x => x.Title, SearchOperator.Contains, "x")
            .Build();

        req.Query.Clauses.Single().ClauseType.Should().Be(SearchClauseType.Should);
    }

    [Fact]
    public void Not_ProducesMustNotClause()
    {
        var req = Query.For<Article>()
            .Not(x => x.Title, SearchOperator.Contains, "x")
            .Build();

        req.Query.Clauses.Single().ClauseType.Should().Be(SearchClauseType.MustNot);
    }

    // ── Build: operator passthrough ───────────────────────────────────────────

    [Theory]
    [InlineData(SearchOperator.Contains)]
    [InlineData(SearchOperator.Equals)]
    [InlineData(SearchOperator.StartsWith)]
    [InlineData(SearchOperator.GreaterThan)]
    [InlineData(SearchOperator.LessThan)]
    [InlineData(SearchOperator.GreaterThanOrEquals)]
    [InlineData(SearchOperator.LessThanOrEquals)]
    [InlineData(SearchOperator.NotEquals)]
    public void Where_PreservesOperator(SearchOperator op)
    {
        var req = Query.For<Article>()
            .Where(x => x.Title, op, "x")
            .Build();

        req.Query.Clauses.Single().Operator.Should().Be(op);
    }

    // ── Build: value encoding ─────────────────────────────────────────────────

    [Fact]
    public void Where_EncodesStringValue_AsStringVal()
    {
        var req = Query.For<Article>()
            .Where(x => x.Title, SearchOperator.Contains, "hello")
            .Build();

        req.Query.Clauses.Single().Value.StringVal.Should().Be("hello");
    }

    [Fact]
    public void Where_EncodesIntValue_AsNumberVal()
    {
        var req = Query.For<Article>()
            .Where(x => x.PageCount, SearchOperator.GreaterThan, 100)
            .Build();

        req.Query.Clauses.Single().Value.NumberVal.Should().Be(100);
    }

    [Fact]
    public void Where_EncodesDoubleValue_AsNumberVal()
    {
        var req = Query.For<Article>()
            .Where(x => x.Rating, SearchOperator.GreaterThan, 4.5)
            .Build();

        req.Query.Clauses.Single().Value.NumberVal.Should().BeApproximately(4.5, 0.001);
    }

    [Fact]
    public void Where_EncodesBoolValue_AsBoolVal()
    {
        var req = Query.For<Article>()
            .Where(x => x.Published, SearchOperator.Equals, true)
            .Build();

        req.Query.Clauses.Single().Value.BoolVal.Should().BeTrue();
    }

    [Fact]
    public void Where_EncodesDateTimeValue_AsIso8601StringVal()
    {
        var dt  = new DateTime(2024, 3, 15, 12, 0, 0, DateTimeKind.Utc);
        var req = Query.For<Article>()
            .Where(x => x.PublishedAt, SearchOperator.GreaterThan, dt)
            .Build();

        req.Query.Clauses.Single().Value.StringVal.Should().Contain("2024-03-15");
    }

    [Fact]
    public void Where_WithStringList_EncodesAsStringList()
    {
        var req = Query.For<Article>()
            .Where(x => x.Author, SearchOperator.In, (object)new[] { "Alice", "Bob" })
            .Build();

        req.Query.Clauses.Single().Value.StringList.Values.Should().BeEquivalentTo(["Alice", "Bob"]);
    }

    // ── Build: vector clauses ─────────────────────────────────────────────────

    [Fact]
    public void WhereVectorSimilar_ProducesFilterClause_WithFloatList()
    {
        var vec = new float[] { 0.1f, 0.2f, 0.3f };
        var req = Query.For<Article>()
            .WhereVectorSimilar(x => x.Title, vec)
            .Build();

        var clause = req.Query.Clauses.Single();
        clause.ClauseType.Should().Be(SearchClauseType.Filter);
        clause.Operator.Should().Be(SearchOperator.VectorSimilar);
        clause.Value.FloatList.Values.Should().BeEquivalentTo(vec);
    }

    [Fact]
    public void AndVectorSimilar_ProducesMustClause()
    {
        var req = Query.For<Article>()
            .AndVectorSimilar(x => x.Title, [0.1f])
            .Build();

        req.Query.Clauses.Single().ClauseType.Should().Be(SearchClauseType.Must);
    }

    // ── Build: sorting ────────────────────────────────────────────────────────

    [Fact]
    public void OrderBy_AddsSort_Ascending()
    {
        var req = Query.For<Article>()
            .OrderBy(x => x.Title)
            .Build();

        req.Query.Sort.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Property = "Title", Descending = false });
    }

    [Fact]
    public void OrderBy_AddsSort_Descending()
    {
        var req = Query.For<Article>()
            .OrderBy(x => x.Rating, descending: true)
            .Build();

        req.Query.Sort.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Property = "Rating", Descending = true });
    }

    [Fact]
    public void OrderBy_MultipleSorts_ArePreservedInOrder()
    {
        var req = Query.For<Article>()
            .OrderBy(x => x.Author)
            .OrderBy(x => x.Rating, descending: true)
            .Build();

        req.Query.Sort.Should().HaveCount(2);
        req.Query.Sort[0].Property.Should().Be("Author");
        req.Query.Sort[1].Property.Should().Be("Rating");
    }

    // ── Build: paging ─────────────────────────────────────────────────────────

    [Fact]
    public void Build_DefaultsToPage1Size20()
    {
        var req = Query.For<Article>().Build();

        req.Page.Should().Be(1);
        req.PageSize.Should().Be(20);
    }

    [Fact]
    public void Page_SetsPagingValues()
    {
        var req = Query.For<Article>().Page(3, 50).Build();

        req.Page.Should().Be(3);
        req.PageSize.Should().Be(50);
    }

    // ── Build: logic ──────────────────────────────────────────────────────────

    [Fact]
    public void Build_DefaultsToAndLogic()
    {
        var req = Query.For<Article>().Build();

        req.Query.Logic.Should().Be(SearchLogic.And);
    }

    [Fact]
    public void WithLogic_SetsOrLogic()
    {
        var req = Query.For<Article>().WithLogic(SearchLogic.Or).Build();

        req.Query.Logic.Should().Be(SearchLogic.Or);
    }

    // ── Build: multiple clauses ───────────────────────────────────────────────

    [Fact]
    public void Build_PreservesAllClausesInOrder()
    {
        var req = Query.For<Article>()
            .Where(x => x.Author,    SearchOperator.Equals,   "Alice")
            .And(x => x.PageCount,   SearchOperator.GreaterThan, 100)
            .Not(x => x.Published,   SearchOperator.Equals,   false)
            .Build();

        req.Query.Clauses.Should().HaveCount(3);
        req.Query.Clauses[0].Property.Should().Be("Author");
        req.Query.Clauses[1].Property.Should().Be("PageCount");
        req.Query.Clauses[2].Property.Should().Be("Published");
    }

    // ── BuildAggregate: terms ─────────────────────────────────────────────────

    [Fact]
    public void GroupBy_AddsTermsAgg_WithAutoName()
    {
        var req = Query.For<Article>().GroupBy(x => x.Author, size: 5).BuildAggregate();

        var agg = req.Aggregations.Should().ContainSingle().Subject;
        agg.Name.Should().Be("Author_terms");
        agg.Type.Should().Be(AggregationType.Terms);
        agg.Field.Should().Be("Author");
        agg.Size.Should().Be(5);
    }

    [Fact]
    public void ByDateInterval_AddsDateHistogramAgg()
    {
        var req = Query.For<Article>()
            .ByDateInterval(x => x.PublishedAt, "month", "America/New_York")
            .BuildAggregate();

        var agg = req.Aggregations.Single();
        agg.Name.Should().Be("PublishedAt_date_histogram");
        agg.Type.Should().Be(AggregationType.DateHistogram);
        agg.CalendarInterval.Should().Be("month");
        agg.TimeZone.Should().Be("America/New_York");
    }

    [Fact]
    public void ByDateInterval_OmitsTimeZone_WhenNotProvided()
    {
        var req = Query.For<Article>()
            .ByDateInterval(x => x.PublishedAt, "day")
            .BuildAggregate();

        req.Aggregations.Single().TimeZone.Should().BeEmpty();
    }

    [Fact]
    public void ByRange_AddsRangeAgg_WithBuckets()
    {
        var req = Query.For<Article>()
            .ByRange(x => x.Rating,
                ("low",    null,  3.0),
                ("medium",  3.0,  4.0),
                ("high",    4.0,  null))
            .BuildAggregate();

        var agg = req.Aggregations.Single();
        agg.Name.Should().Be("Rating_range");
        agg.Type.Should().Be(AggregationType.Range);
        agg.RangeBuckets.Should().HaveCount(3);
        agg.RangeBuckets[0].Key.Should().Be("low");
        agg.RangeBuckets[0].From.Should().BeNull();
        agg.RangeBuckets[0].To.Should().Be(3.0);
        agg.RangeBuckets[2].Key.Should().Be("high");
        agg.RangeBuckets[2].From.Should().Be(4.0);
        agg.RangeBuckets[2].To.Should().BeNull();
    }

    // ── BuildAggregate: metrics ───────────────────────────────────────────────

    [Theory]
    [InlineData("Avg",         AggregationType.Avg)]
    [InlineData("Sum",         AggregationType.Sum)]
    [InlineData("Min",         AggregationType.Min)]
    [InlineData("Max",         AggregationType.Max)]
    [InlineData("Cardinality", AggregationType.Cardinality)]
    public void MetricMethods_AddCorrectAggType(string method, AggregationType expectedType)
    {
        var req = method switch
        {
            "Avg"         => Query.For<Article>().Avg(x => x.Rating).BuildAggregate(),
            "Sum"         => Query.For<Article>().Sum(x => x.PageCount).BuildAggregate(),
            "Min"         => Query.For<Article>().Min(x => x.Rating).BuildAggregate(),
            "Max"         => Query.For<Article>().Max(x => x.Rating).BuildAggregate(),
            "Cardinality" => Query.For<Article>().CountDistinct(x => x.Author).BuildAggregate(),
            _             => throw new InvalidOperationException()
        };

        req.Aggregations.Single().Type.Should().Be(expectedType);
    }

    [Fact]
    public void MetricAgg_AutoName_IsFieldUnderscoreType()
    {
        var req = Query.For<Article>().Avg(x => x.Rating).BuildAggregate();

        req.Aggregations.Single().Name.Should().Be("Rating_avg");
    }

    [Fact]
    public void BuildAggregate_MultipleAggs_AreAllIncluded()
    {
        var req = Query.For<Article>()
            .GroupBy(x => x.Author)
            .Avg(x => x.Rating)
            .Sum(x => x.PageCount)
            .BuildAggregate();

        req.Aggregations.Should().HaveCount(3);
    }

    // ── BuildAggregate: filter clauses ────────────────────────────────────────

    [Fact]
    public void BuildAggregate_IncludesFilterClauses_InQuery()
    {
        var req = Query.For<Article>()
            .Where(x => x.Published, SearchOperator.Equals, true)
            .GroupBy(x => x.Author)
            .BuildAggregate();

        req.Query.Clauses.Should().ContainSingle()
            .Which.Property.Should().Be("Published");
    }

    [Fact]
    public void BuildAggregate_SetsTypeName()
    {
        var req = Query.For<Article>().GroupBy(x => x.Author).BuildAggregate();

        req.TypeName.Should().Be("Article");
    }

    [Fact]
    public void BuildAggregate_SetsTraceId_WhenProvided()
    {
        var req = Query.For<Article>()
            .GroupBy(x => x.Author)
            .BuildAggregate(traceId: "trace-123");

        req.TraceId.Should().Be("trace-123");
    }
}
