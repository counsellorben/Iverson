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
        var filter = IntelligenceFilterBuilder.Build(
            [Clause("category", SearchOperator.Equals, Str("Tech"))], SearchLogic.And, "SearchSimilar");

        filter.Must.Should().ContainSingle();
        filter.Should.Should().BeEmpty();
        filter.MustNot.Should().BeEmpty();
    }

    [Fact]
    public void Build_EqualsBool_ProducesMatch()
    {
        var filter = IntelligenceFilterBuilder.Build(
            [Clause("featured", SearchOperator.Equals, Bool(true))], SearchLogic.And, "SearchSimilar");

        filter.Must.Should().ContainSingle();
    }

    [Fact]
    public void Build_EqualsNumber_ProducesMatch()
    {
        var filter = IntelligenceFilterBuilder.Build(
            [Clause("wordCount", SearchOperator.Equals, Num(500))], SearchLogic.And, "SearchSimilar");

        filter.Must.Should().ContainSingle();
    }

    [Fact]
    public void Build_NotEquals_RoutesToMustNot()
    {
        var filter = IntelligenceFilterBuilder.Build(
            [Clause("category", SearchOperator.NotEquals, Str("Tech"))], SearchLogic.And, "SearchSimilar");

        filter.MustNot.Should().ContainSingle();
        filter.Must.Should().BeEmpty();
    }

    [Fact]
    public void Build_MustNotClauseType_RoutesToMustNot()
    {
        var filter = IntelligenceFilterBuilder.Build(
            [Clause("category", SearchOperator.Equals, Str("Tech"), SearchClauseType.MustNot)],
            SearchLogic.And, "SearchSimilar");

        filter.MustNot.Should().ContainSingle();
    }

    [Fact]
    public void Build_NotEqualsAndMustNotClauseType_DoubleNegative_RoutesToMust()
    {
        var filter = IntelligenceFilterBuilder.Build(
            [Clause("category", SearchOperator.NotEquals, Str("Tech"), SearchClauseType.MustNot)],
            SearchLogic.And, "SearchSimilar");

        filter.Must.Should().ContainSingle();
        filter.MustNot.Should().BeEmpty();
    }

    [Fact]
    public void Build_GreaterThan_ProducesRangeCondition()
    {
        var filter = IntelligenceFilterBuilder.Build(
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
        var act = () => IntelligenceFilterBuilder.Build([Clause("wordCount", op, Num(100))], SearchLogic.And, "SearchSimilar");
        act.Should().NotThrow();
    }

    [Fact]
    public void Build_In_ProducesMatchAnyCondition()
    {
        var filter = IntelligenceFilterBuilder.Build(
            [Clause("category", SearchOperator.In, List("Tech", "Science"))], SearchLogic.And, "SearchSimilar");

        filter.Must.Should().ContainSingle();
    }

    [Fact]
    public void Build_OrLogic_RoutesPositiveClausesToShould()
    {
        var filter = IntelligenceFilterBuilder.Build(
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
        var filter = IntelligenceFilterBuilder.Build(
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

        var act = () => IntelligenceFilterBuilder.Build([Clause("title", op, value)], SearchLogic.And, "SearchSimilar");

        act.Should().Throw<FilterTranslationException>()
            .Where(e => e.Message.Contains(op.ToString()) && e.Message.Contains("SearchSimilar"));
    }

    [Fact]
    public void Build_InWithNonListValue_Throws()
    {
        // A caller could send an IN clause whose Value isn't actually a StringList (proto3
        // message field defaults to null when unset). Accessing .StringList.Values on that
        // would NullReferenceException — must be a clean exception instead.
        var act = () => IntelligenceFilterBuilder.Build(
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
        var act = () => IntelligenceFilterBuilder.Build(
            [Clause("wordCount", op, Str("not-a-number"))], SearchLogic.And, "SearchSimilar");

        act.Should().Throw<FilterTranslationException>()
            .Where(e => e.Message.Contains(op.ToString()) && e.Message.Contains("wordCount"));
    }

    // ---- Timestamp canonicalization -------------------------------------------------------
    // IntelligenceStoreConsumer stores timestamp payload values in round-trip ("o") form, so the
    // filter operand must be re-emitted the same way or string equality never matches.

    private const string NonCanonicalTimestamp = "2026-07-29T00:00:00Z";
    private const string CanonicalTimestamp    = "2026-07-29T00:00:00.0000000+00:00";

    // camelCase: property names reach the builder already camelCased from ObjectSearchGrpcService,
    // and the set is built from camelCased column names. A casing mismatch here would silently
    // disable canonicalization altogether.
    private static readonly IReadOnlySet<string> TimestampColumns =
        new HashSet<string>(StringComparer.Ordinal) { "publishedAt" };

    [Fact]
    public void Build_EqualsOnTimestampColumn_CanonicalizesOperand()
    {
        var filter = IntelligenceFilterBuilder.Build(
            [Clause("publishedAt", SearchOperator.Equals, Str(NonCanonicalTimestamp))],
            SearchLogic.And, "SearchSimilar", TimestampColumns);

        filter.Must.Should().ContainSingle();
        filter.Must[0].Field.Match.Keyword.Should().Be(CanonicalTimestamp);
    }

    [Fact]
    public void Build_NotEqualsOnTimestampColumn_CanonicalizesOperand()
    {
        var filter = IntelligenceFilterBuilder.Build(
            [Clause("publishedAt", SearchOperator.NotEquals, Str(NonCanonicalTimestamp))],
            SearchLogic.And, "SearchSimilar", TimestampColumns);

        filter.MustNot.Should().ContainSingle();
        filter.MustNot[0].Field.Match.Keyword.Should().Be(CanonicalTimestamp);
    }

    [Fact]
    public void Build_InOnTimestampColumn_CanonicalizesEveryElement()
    {
        // The IN arm does not route through the equality helper — it is a second, independent
        // string-comparison emission point.
        var filter = IntelligenceFilterBuilder.Build(
            [Clause("publishedAt", SearchOperator.In, List(NonCanonicalTimestamp, "2026-07-30T12:30:00+02:00"))],
            SearchLogic.And, "SearchSimilar", TimestampColumns);

        filter.Must.Should().ContainSingle();
        filter.Must[0].Field.Match.Keywords.Strings.Should().Equal(
            CanonicalTimestamp, "2026-07-30T10:30:00.0000000+00:00");   // +02:00 normalized to UTC
    }

    [Fact]
    public void Build_SameInstantWithDifferentOffsets_CanonicalizesIdentically()
    {
        // The whole point of canonicalization: an operand naming the same INSTANT must produce the
        // same string whatever offset the caller expressed it in, or equality silently no-hits.
        static string Canonical(string operand) =>
            IntelligenceFilterBuilder.Build(
                [Clause("publishedAt", SearchOperator.Equals, Str(operand))],
                SearchLogic.And, "SearchSimilar", TimestampColumns)
                .Must[0].Field.Match.Keyword;

        Canonical("2026-07-30T12:30:00+02:00").Should().Be("2026-07-30T10:30:00.0000000+00:00");
        Canonical("2026-07-30T10:30:00Z").Should().Be("2026-07-30T10:30:00.0000000+00:00");
        Canonical("2026-07-30T05:30:00-05:00").Should().Be("2026-07-30T10:30:00.0000000+00:00");
    }

    [Fact]
    public void Build_OffsetLessTimestampOperand_IsReadAsUtc_NotMachineLocalTime()
    {
        // AssumeUniversal: without it the value would be resolved against the pod's local
        // timezone, so the API pod and the ingest pod would disagree under different TZ settings.
        var filter = IntelligenceFilterBuilder.Build(
            [Clause("publishedAt", SearchOperator.Equals, Str("2026-07-30T10:30:00"))],
            SearchLogic.And, "SearchSimilar", TimestampColumns);

        filter.Must[0].Field.Match.Keyword.Should().Be("2026-07-30T10:30:00.0000000+00:00");
    }

    [Fact]
    public void Build_TimestampColumnNameCasingMismatch_LeavesOperandUntouched()
    {
        // Guards the casing contract: property names arrive camelCased, so a PascalCase set entry
        // matches nothing and the bug silently returns.
        var pascalCased = new HashSet<string>(StringComparer.Ordinal) { "PublishedAt" };

        var filter = IntelligenceFilterBuilder.Build(
            [Clause("publishedAt", SearchOperator.Equals, Str(NonCanonicalTimestamp))],
            SearchLogic.And, "SearchSimilar", pascalCased);

        filter.Must[0].Field.Match.Keyword.Should().Be(NonCanonicalTimestamp);
    }

    [Fact]
    public void Build_EqualsOnNonTimestampColumn_LeavesOperandUntouched()
    {
        var filter = IntelligenceFilterBuilder.Build(
            [Clause("category", SearchOperator.Equals, Str(NonCanonicalTimestamp))],
            SearchLogic.And, "SearchSimilar", TimestampColumns);

        filter.Must[0].Field.Match.Keyword.Should().Be(NonCanonicalTimestamp);
    }

    [Fact]
    public void Build_UnparseableTimestampOperand_PassesThroughUnchanged()
    {
        var filter = IntelligenceFilterBuilder.Build(
            [Clause("publishedAt", SearchOperator.Equals, Str("not-a-timestamp"))],
            SearchLogic.And, "SearchSimilar", TimestampColumns);

        filter.Must[0].Field.Match.Keyword.Should().Be("not-a-timestamp");
    }

    [Fact]
    public void Build_TimestampColumnsOmitted_BehavesAsBefore()
    {
        var filter = IntelligenceFilterBuilder.Build(
            [Clause("publishedAt", SearchOperator.Equals, Str(NonCanonicalTimestamp))],
            SearchLogic.And, "SearchSimilar");

        filter.Must[0].Field.Match.Keyword.Should().Be(NonCanonicalTimestamp);
    }

    [Fact]
    public void MatchEquality_TimestampColumn_CanonicalizesOperand()
    {
        // SearchChunks enters the builder here, not through Build — the only case that fails if
        // this second entry point is left unthreaded.
        var condition = IntelligenceFilterBuilder.MatchEquality(
            "publishedAt", Str(NonCanonicalTimestamp), TimestampColumns);

        condition.Field.Key.Should().Be("publishedAt");
        condition.Field.Match.Keyword.Should().Be(CanonicalTimestamp);
    }

    [Fact]
    public void MatchEquality_TimestampColumnsOmitted_BehavesAsBefore()
    {
        var condition = IntelligenceFilterBuilder.MatchEquality("publishedAt", Str(NonCanonicalTimestamp));

        condition.Field.Match.Keyword.Should().Be(NonCanonicalTimestamp);
    }

    [Fact]
    public void MatchParentId_ProducesSingleMustMatchKeywordOnParentId()
    {
        var filter = IntelligenceFilterBuilder.MatchParentId("parent-123");

        filter.Must.Should().ContainSingle();
        filter.Must[0].Field.Key.Should().Be("parent_id");
        filter.Must[0].Field.Match.Keyword.Should().Be("parent-123");
        filter.Should.Should().BeEmpty();
        filter.MustNot.Should().BeEmpty();
    }

    [Fact]
    public void ApplyOwnership_NotRequired_NullFilter_ReturnsNull()
    {
        var result = IntelligenceFilterBuilder.ApplyOwnership(null, ownershipRequired: false, "ownerId", "owner-1");

        result.Should().BeNull();
    }

    [Fact]
    public void ApplyOwnership_NotRequired_ExistingFilter_ReturnsSameFilterUnchanged()
    {
        var original = new Filter();
        original.Must.Add(Conditions.MatchKeyword("category", "Tech"));

        var result = IntelligenceFilterBuilder.ApplyOwnership(original, ownershipRequired: false, "ownerId", "owner-1");

        result.Should().BeSameAs(original);
        result!.Must.Should().ContainSingle();
    }

    [Fact]
    public void ApplyOwnership_Required_NullFilter_CreatesFilterWithMatchKeywordCondition()
    {
        var result = IntelligenceFilterBuilder.ApplyOwnership(null, ownershipRequired: true, "ownerId", "owner-1");

        result.Should().NotBeNull();
        result!.Must.Should().ContainSingle();
        result.Must[0].Field.Key.Should().Be("ownerId");
        result.Must[0].Field.Match.Keyword.Should().Be("owner-1");
    }

    [Fact]
    public void ApplyOwnership_Required_ExistingFilter_AppendsConditionPreservingExisting()
    {
        var original = new Filter();
        original.Must.Add(Conditions.MatchKeyword("category", "Tech"));

        var result = IntelligenceFilterBuilder.ApplyOwnership(original, ownershipRequired: true, "ownerId", "owner-1");

        result.Should().BeSameAs(original);
        result!.Must.Should().HaveCount(2);
    }
}
