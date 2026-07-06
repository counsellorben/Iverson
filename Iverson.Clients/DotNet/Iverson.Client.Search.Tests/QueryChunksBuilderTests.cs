using FluentAssertions;
using Iverson.Client.Contracts;
using Xunit;
using static Iverson.Client.Search.SearchOperators;

namespace Iverson.Client.Search.Tests;

public class QueryChunksBuilderTests
{
    private sealed class TestArticle
    {
        public string Id { get; set; } = "";
        public string Body { get; set; } = "";
    }

    [Fact]
    public void Build_HappyPath_ProducesExpectedRequest()
    {
        SearchChunksRequest request = Query.Chunks<TestArticle>(a => a.Body)
            .Text("neural networks")
            .TopK(5)
            .Where(a => a.Id, EqualTo, "parent-123")
            .Build();

        request.TypeName.Should().Be("TestArticle");
        request.Property.Should().Be("Body");
        request.TopK.Should().Be(5u);
        request.Filter.Should().ContainSingle(c => c.Property == "Id" && c.Operator == SearchOperator.Equals);
    }

    [Fact]
    public void Where_NonEqualsOperator_Throws()
    {
        var act = () => Query.Chunks<TestArticle>(a => a.Body).Where(a => a.Id, GreaterThan, "x");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Where_CalledTwice_ThrowsAtBuildTime()
    {
        var builder = Query.Chunks<TestArticle>(a => a.Body)
            .Where(a => a.Id, EqualTo, "a")
            .Where(a => a.Id, EqualTo, "b");
        var act = () => builder.Build();
        act.Should().Throw<InvalidOperationException>().WithMessage("*at most one*");
    }
}
