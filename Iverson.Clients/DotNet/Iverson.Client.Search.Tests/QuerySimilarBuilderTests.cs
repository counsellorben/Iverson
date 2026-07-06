using FluentAssertions;
using Iverson.Client.Contracts;
using Xunit;
using static Iverson.Client.Search.SearchOperators;

namespace Iverson.Client.Search.Tests;

public class QuerySimilarBuilderTests
{
    private sealed class TestArticle
    {
        public string Title { get; set; } = "";
        public string Category { get; set; } = "";
    }

    [Fact]
    public void Build_HappyPath_ProducesExpectedRequest()
    {
        SearchSimilarRequest request = Query.Similar<TestArticle>(a => a.Title)
            .Text("machine learning")
            .TopK(10)
            .Where(a => a.Category, EqualTo, "Tech")
            .Build();

        request.TypeName.Should().Be("TestArticle");
        request.Property.Should().Be("Title");
        request.Query.Should().Be("machine learning");
        request.TopK.Should().Be(10u);
        request.Filter.Should().ContainSingle(c => c.Property == "Category" && c.Operator == SearchOperator.Equals);
    }

    [Fact]
    public void Build_NoFilter_ProducesEmptyFilterList()
    {
        var request = Query.Similar<TestArticle>(a => a.Title).Text("x").Build();
        request.Filter.Should().BeEmpty();
    }

    [Theory]
    [InlineData(SearchOperator.Contains)]
    [InlineData(SearchOperator.StartsWith)]
    [InlineData(SearchOperator.EndsWith)]
    [InlineData(SearchOperator.VectorSimilar)]
    public void Where_UnsupportedOperator_Throws(SearchOperator op)
    {
        var act = () => Query.Similar<TestArticle>(a => a.Title).Where(a => a.Category, op, "x");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void WithLogic_Or_SetsFilterLogic()
    {
        var request = Query.Similar<TestArticle>(a => a.Title)
            .Where(a => a.Category, EqualTo, "Tech")
            .Where(a => a.Category, EqualTo, "Science")
            .WithLogic(SearchLogic.Or)
            .Build();

        request.FilterLogic.Should().Be(SearchLogic.Or);
    }
}
