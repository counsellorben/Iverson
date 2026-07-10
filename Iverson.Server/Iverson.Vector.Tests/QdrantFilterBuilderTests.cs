using FluentAssertions;
using Iverson.Client.Contracts;
using Qdrant.Client.Grpc;
using Xunit;

namespace Iverson.Vector.Tests;

public class QdrantFilterBuilderTests
{
    private static SearchClause Clause(string property, SearchOperator op, SearchValue value,
        SearchClauseType clauseType = SearchClauseType.Filter) => new()
    {
        Property = property, Operator = op, Value = value, ClauseType = clauseType
    };

    private static SearchValue Str(string s)   => new() { StringVal = s };
    private static SearchValue Num(double n)   => new() { NumberVal = n };
    private static SearchValue Bool(bool b)    => new() { BoolVal = b };
    private static SearchValue List(params string[] vals)
    {
        var v = new SearchValue { StringList = new RepeatedString() };
        v.StringList.Values.AddRange(vals);
        return v;
    }

    [Fact]
    public void Build_EqualsString_ProducesMatchKeyword()
    {
        var filter = QdrantFilterBuilder.Build(
            [Clause("category", SearchOperator.Equals, Str("Tech"))], SearchLogic.And, "SearchSimilar");

        filter.Must.Should().ContainSingle();
        filter.Should.Should().BeEmpty();
        filter.MustNot.Should().BeEmpty();
    }

    [Fact]
    public void Build_EqualsBool_ProducesMatch()
    {
        var filter = QdrantFilterBuilder.Build(
            [Clause("featured", SearchOperator.Equals, Bool(true))], SearchLogic.And, "SearchSimilar");

        filter.Must.Should().ContainSingle();
    }

    [Fact]
    public void Build_EqualsNumber_ProducesMatch()
    {
        var filter = QdrantFilterBuilder.Build(
            [Clause("wordCount", SearchOperator.Equals, Num(500))], SearchLogic.And, "SearchSimilar");

        filter.Must.Should().ContainSingle();
    }

    [Fact]
    public void Build_NotEquals_RoutesToMustNot()
    {
        var filter = QdrantFilterBuilder.Build(
            [Clause("category", SearchOperator.NotEquals, Str("Tech"))], SearchLogic.And, "SearchSimilar");

        filter.MustNot.Should().ContainSingle();
        filter.Must.Should().BeEmpty();
    }

    [Fact]
    public void Build_MustNotClauseType_RoutesToMustNot()
    {
        var filter = QdrantFilterBuilder.Build(
            [Clause("category", SearchOperator.Equals, Str("Tech"), SearchClauseType.MustNot)],
            SearchLogic.And, "SearchSimilar");

        filter.MustNot.Should().ContainSingle();
    }

    [Fact]
    public void Build_NotEqualsAndMustNotClauseType_DoubleNegative_RoutesToMust()
    {
        var filter = QdrantFilterBuilder.Build(
            [Clause("category", SearchOperator.NotEquals, Str("Tech"), SearchClauseType.MustNot)],
            SearchLogic.And, "SearchSimilar");

        filter.Must.Should().ContainSingle();
        filter.MustNot.Should().BeEmpty();
    }

    [Fact]
    public void Build_GreaterThan_ProducesRangeCondition()
    {
        var filter = QdrantFilterBuilder.Build(
            [Clause("wordCount", SearchOperator.GreaterThan, Num(100))], SearchLogic.And, "SearchSimilar");

        filter.Must.Should().ContainSingle();
    }

    [Theory]
    [InlineData(SearchOperator.GreaterThan)]
    [InlineData(SearchOperator.LessThan)]
    [InlineData(SearchOperator.GreaterThanOrEquals)]
    [InlineData(SearchOperator.LessThanOrEquals)]
    public void Build_RangeOperators_DoNotThrow(SearchOperator op)
    {
        var act = () => QdrantFilterBuilder.Build([Clause("wordCount", op, Num(100))], SearchLogic.And, "SearchSimilar");
        act.Should().NotThrow();
    }

    [Fact]
    public void Build_In_ProducesMatchAnyCondition()
    {
        var filter = QdrantFilterBuilder.Build(
            [Clause("category", SearchOperator.In, List("Tech", "Science"))], SearchLogic.And, "SearchSimilar");

        filter.Must.Should().ContainSingle();
    }

    [Fact]
    public void Build_OrLogic_RoutesPositiveClausesToShould()
    {
        var filter = QdrantFilterBuilder.Build(
            [Clause("category", SearchOperator.Equals, Str("Tech")),
             Clause("category", SearchOperator.Equals, Str("Science"))],
            SearchLogic.Or, "SearchSimilar");

        filter.Should.Should().HaveCount(2);
        filter.Must.Should().BeEmpty();
    }

    [Fact]
    public void Build_OrLogicWithNegatedClause_KeepsNegationInsideShould()
    {
        // Regression test: under OR logic, a NotEquals clause must be nested (negated) inside
        // the top-level Should group — not routed to top-level MustNot, which Qdrant ANDs
        // against everything else and would silently turn "A OR NOT B" into "A AND NOT B".
        var filter = QdrantFilterBuilder.Build(
            [Clause("category", SearchOperator.Equals, Str("Tech")),
             Clause("category", SearchOperator.NotEquals, Str("Science"))],
            SearchLogic.Or, "SearchSimilar");

        filter.Should.Should().HaveCount(2);
        filter.Must.Should().BeEmpty();
        filter.MustNot.Should().BeEmpty();

        // The negated clause must be nested (a Filter-typed Condition wrapping the original
        // in MustNot), not a bare positive condition — proves the `!` operator was actually applied,
        // not just that something landed in Should.
        var negatedEntry = filter.Should[1];
        negatedEntry.Filter.Should().NotBeNull();
        negatedEntry.Filter.MustNot.Should().ContainSingle(c => c.Field.Key == "category");
    }

    [Theory]
    [InlineData(SearchOperator.Contains)]
    [InlineData(SearchOperator.StartsWith)]
    [InlineData(SearchOperator.EndsWith)]
    [InlineData(SearchOperator.VectorSimilar)]
    public void Build_UnsupportedOperator_ThrowsNamingOperatorAndRpc(SearchOperator op)
    {
        var value = op == SearchOperator.VectorSimilar
            ? new SearchValue { FloatList = new RepeatedFloat { Values = { 0.1f } } }
            : Str("x");

        var act = () => QdrantFilterBuilder.Build([Clause("title", op, value)], SearchLogic.And, "SearchSimilar");

        act.Should().Throw<FilterTranslationException>()
            .Where(e => e.Message.Contains(op.ToString()) && e.Message.Contains("SearchSimilar"));
    }

    [Fact]
    public void Build_InWithNonListValue_Throws()
    {
        // A caller could send an IN clause whose Value isn't actually a StringList (proto3
        // message field defaults to null when unset). Accessing .StringList.Values on that
        // would NullReferenceException — must be a clean exception instead.
        var act = () => QdrantFilterBuilder.Build(
            [Clause("category", SearchOperator.In, Str("Tech"))], SearchLogic.And, "SearchSimilar");

        act.Should().Throw<FilterTranslationException>()
            .Where(e => e.Message.Contains(SearchOperator.In.ToString()) && e.Message.Contains("category"));
    }

    [Theory]
    [InlineData(SearchOperator.GreaterThan)]
    [InlineData(SearchOperator.LessThan)]
    [InlineData(SearchOperator.GreaterThanOrEquals)]
    [InlineData(SearchOperator.LessThanOrEquals)]
    public void Build_RangeOperatorWithNonNumericValue_Throws(SearchOperator op)
    {
        // A caller could send a Range clause whose Value isn't actually a NumberVal (proto3
        // scalar field silently defaults to 0 when a different oneof member is set) — must be
        // a clean exception rather than silently filtering on 0.
        var act = () => QdrantFilterBuilder.Build(
            [Clause("wordCount", op, Str("not-a-number"))], SearchLogic.And, "SearchSimilar");

        act.Should().Throw<FilterTranslationException>()
            .Where(e => e.Message.Contains(op.ToString()) && e.Message.Contains("wordCount"));
    }

    [Fact]
    public void MatchParentId_ProducesSingleMustMatchKeywordOnParentId()
    {
        var filter = QdrantFilterBuilder.MatchParentId("parent-123");

        filter.Must.Should().ContainSingle();
        filter.Must[0].Field.Key.Should().Be("parent_id");
        filter.Must[0].Field.Match.Keyword.Should().Be("parent-123");
        filter.Should.Should().BeEmpty();
        filter.MustNot.Should().BeEmpty();
    }
}
