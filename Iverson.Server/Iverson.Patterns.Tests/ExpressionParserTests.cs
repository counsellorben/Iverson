using FluentAssertions;
using Iverson.Patterns.Expressions;
using Xunit;

namespace Iverson.Patterns.Tests;

public sealed class ExpressionParserTests
{
    // Variables A=0, B=1; subset U = {A, B}.
    private static ExpressionParser NewParser(SimilarityTermTable? terms = null) =>
        new(new Dictionary<string, int> { ["A"] = 0, ["B"] = 1 },
            new Dictionary<string, int[]> { ["U"] = [0, 1] },
            terms ?? new SimilarityTermTable());

    private static Expr Measure(string text) => NewParser().ParseMeasure(text);
    private static Expr Define(string text) => NewParser().ParseDefine(text);

    private static Navigation Nav(int[] labels, bool last = true, bool running = true, int logical = 0, int physical = 0) =>
        new(labels, last, running, logical, physical);

    [Theory]
    [InlineData("x", new int[0], true, 0, 0)]
    [InlineData("A.x", new[] { 0 }, true, 0, 0)]
    [InlineData("a.x", new[] { 0 }, true, 0, 0)]
    [InlineData("U.x", new[] { 0, 1 }, true, 0, 0)]
    [InlineData("PREV(A.x)", new[] { 0 }, true, 0, -1)]
    [InlineData("NEXT(A.x, 3)", new[] { 0 }, true, 0, 3)]
    [InlineData("FIRST(A.x)", new[] { 0 }, false, 0, 0)]
    [InlineData("FIRST(A.x, 2)", new[] { 0 }, false, 2, 0)]
    [InlineData("LAST(x, 1)", new int[0], true, 1, 0)]
    [InlineData("PREV(FIRST(A.x, 2), 3)", new[] { 0 }, false, 2, -3)]
    [InlineData("NEXT(LAST(B.x))", new[] { 1 }, true, 0, 1)]
    public void Column_reads_lower_to_navigations(string text, int[] labels, bool last, int logical, int physical) =>
        Measure(text).Should().BeOfType<ColumnExpr>()
            .Which.Navigation.Should().Be(Nav(labels, last, running: true, logical, physical));

    [Fact]
    public void Final_applies_to_first_last_and_aggregates_in_measures()
    {
        Measure("FINAL LAST(x)").Should().BeOfType<ColumnExpr>()
            .Which.Navigation.Should().Be(Nav([], last: true, running: false));
        Measure("PREV(FINAL FIRST(A.x))").Should().BeOfType<ColumnExpr>()
            .Which.Navigation.Should().Be(Nav([0], last: false, running: false, physical: -1));
        Measure("FINAL COUNT(*)").Should().BeOfType<AggregateExpr>().Which.Running.Should().BeFalse();
        Measure("RUNNING COUNT(*)").Should().BeOfType<AggregateExpr>().Which.Running.Should().BeTrue();
    }

    [Fact]
    public void A_navigation_applies_to_every_read_in_its_argument()
    {
        var sum = Measure("PREV(A.x + A.y, 2)").Should().BeOfType<BinaryExpr>().Subject;
        sum.Left.Should().BeOfType<ColumnExpr>().Which.Navigation.Should().Be(Nav([0], physical: -2));
        sum.Right.Should().BeOfType<ColumnExpr>().Which.Navigation.Should().Be(Nav([0], physical: -2));
    }

    [Fact]
    public void Classifier_and_match_number_lower_to_their_reads()
    {
        Measure("CLASSIFIER()").Should().BeOfType<ClassifierExpr>().Which.Navigation.Should().Be(Nav([]));
        Measure("CLASSIFIER(U)").Should().BeOfType<ClassifierExpr>().Which.Navigation.Should().Be(Nav([0, 1]));
        Measure("PREV(CLASSIFIER(), 1)").Should().BeOfType<ClassifierExpr>()
            .Which.Navigation.Should().Be(Nav([], physical: -1));
        Measure("MATCH_NUMBER()").Should().BeOfType<MatchNumberExpr>();
    }

    [Fact]
    public void Aggregates_read_the_aggregated_row_and_record_their_labels()
    {
        var countAll = Measure("COUNT(*)").Should().BeOfType<AggregateExpr>().Subject;
        countAll.Kind.Should().Be(AggregateKind.Count);
        countAll.Labels.Should().BeEmpty();
        countAll.Argument.Should().BeNull();
        countAll.Running.Should().BeTrue();

        var countA = Measure("count(a.*)").Should().BeOfType<AggregateExpr>().Subject;
        countA.Kind.Should().Be(AggregateKind.Count);
        countA.Labels.Should().Equal(0);
        countA.Argument.Should().BeNull();

        var sum = Measure("SUM(U.price)").Should().BeOfType<AggregateExpr>().Subject;
        sum.Labels.Should().Equal(0, 1);
        sum.Argument.Should().Be(new ColumnExpr("price", null));
        sum.ClassifierInvolved.Should().BeFalse();

        Measure("SUM(x)").Should().BeOfType<AggregateExpr>().Which.Labels.Should().BeEmpty();
        Measure("COUNT(CLASSIFIER())").Should().BeOfType<AggregateExpr>().Which.ClassifierInvolved.Should().BeTrue();
        Measure("SUM(MATCH_NUMBER())").Should().BeOfType<AggregateExpr>();
    }

    [Fact]
    public void Literals_and_operators_lower_with_sql_precedence()
    {
        Measure("1 + 2 * 3").Should().Be(new BinaryExpr(BinaryOp.Add, new LiteralExpr(1L),
            new BinaryExpr(BinaryOp.Multiply, new LiteralExpr(2L), new LiteralExpr(3L))));
        Measure("2.5").Should().Be(new LiteralExpr(2.5));
        Measure("'it''s'").Should().Be(new LiteralExpr("it's"));
        Measure("NULL").Should().Be(new LiteralExpr(null));
        Measure("-x").Should().BeOfType<UnaryExpr>().Which.Op.Should().Be(UnaryOp.Negate);
        Define("x = 1 OR x = 2 AND NOT x = 3").Should().BeOfType<BinaryExpr>().Which.Op.Should().Be(BinaryOp.Or);
        Define("x IS NOT NULL").Should().Be(new IsNullExpr(new ColumnExpr("x", Nav([])), Negated: true));
        Define("x NOT IN (1, 2)").Should().BeOfType<InExpr>().Which.Negated.Should().BeTrue();
        Define("x NOT BETWEEN 1 AND 2").Should().BeOfType<BetweenExpr>().Which.Negated.Should().BeTrue();
        Measure("CASE WHEN x > 1 THEN 'a' ELSE 'b' END").Should().BeOfType<CaseExpr>().Which.Operand.Should().BeNull();
        Measure("CASE x WHEN 1 THEN 'a' END").Should().BeOfType<CaseExpr>().Which.Operand.Should().NotBeNull();
        Measure("coalesce(x, 0)").Should().BeOfType<FunctionExpr>().Which.Function.Should().Be(ScalarFunction.Coalesce);
        Measure("TIMESTAMPDIFF(minute, A.t, B.t)").Should().BeOfType<TimestampDiffExpr>()
            .Which.Unit.Should().Be(TimeUnit.Minute);
    }

    [Fact]
    public void Similarity_terms_are_deduplicated_by_column_case_insensitively_and_text_ordinally()
    {
        var terms = new SimilarityTermTable();
        var parser = NewParser(terms);

        parser.ParseDefine("SIMILARITY(title, 'refund') > 0.5");
        parser.ParseMeasure("SIMILARITY(Title, 'refund')");
        parser.ParseMeasure("SIMILARITY(A.title, 'Refund')");

        // Title/title collapse to one term (column case-insensitive); 'Refund' is a second term (text ordinal).
        terms.Terms.Should().Equal(new SimilarityTerm("title", "refund"), new SimilarityTerm("title", "Refund"));
        parser.ParseMeasure("SIMILARITY(B.title, 'x')").Should().BeOfType<SimilarityExpr>()
            .Which.Navigation.Should().Be(Nav([1]));
    }

    [Fact]
    public void Referenced_columns_accumulate_case_insensitively_across_expressions()
    {
        var parser = NewParser();
        parser.ParseDefine("A.price > PREV(A.Price)");
        parser.ParseMeasure("SUM(B.qty) + SIMILARITY(title, 'x')");

        parser.ReferencedColumns.Should().BeEquivalentTo(["price", "qty", "title"]);
        parser.ReferencedColumns.Contains("PRICE").Should().BeTrue();
    }

    [Theory]
    // Trino 483 analyzer rules (plan-level assumption 25 and the V-probes; messages quoted where Trino's is used).
    [InlineData("PREV(A.x + B.x) > 0", "*must match*")]
    [InlineData("PREV(x + A.x) > 0", "*must match*")]
    [InlineData("FIRST(PREV(A.x)) > 0", "*nest*")]
    [InlineData("FIRST(LAST(A.x)) > 0", "*nest*")]
    [InlineData("PREV(PREV(A.x)) > 0", "*nest*")]
    [InlineData("PREV(NEXT(A.x)) > 0", "*nest*")]
    [InlineData("PREV(FIRST(A.x) + 1) > 0", "*Immediate nesting*")]
    [InlineData("PREV(COUNT(A.*)) > 0", "*nest*")]
    [InlineData("SUM(PREV(A.x)) > 0", "*nest*")]
    [InlineData("SUM(COUNT(A.*)) > 0", "*nest*")]
    [InlineData("SUM(A.x + B.x) > 0", "*must match*")]
    [InlineData("PREV(1) > 0", "*column reference or CLASSIFIER*")]
    [InlineData("PREV(MATCH_NUMBER()) > 0", "*column reference or CLASSIFIER*")]
    [InlineData("PREV(A.x, -1) > 0", "*non-negative*")]
    [InlineData("PREV(A.x, 1 + 1) > 0", "*number*")]
    [InlineData("RUNNING PREV(A.x) > 0", "*RUNNING*")]
    [InlineData("CLASSIFIER(C) = 'A'", "*C*")]
    [InlineData("C.x > 0", "*C*")]
    [InlineData("FINAL LAST(A.x) > 0", "*FINAL*DEFINE*")]
    [InlineData("A.x + 1", "*boolean*")]
    [InlineData("COUNT(*)", "*boolean*")]
    [InlineData("'yes'", "*boolean*")]
    [InlineData("SIMILARITY(title, '') > 0", "*SIMILARITY*")]
    [InlineData("SIMILARITY(title, x) > 0", "*SIMILARITY*")]
    [InlineData("SIMILARITY('t', 'x') > 0", "*SIMILARITY*")]
    [InlineData("TIMESTAMPDIFF(WEEK, A.t, B.t) > 0", "*TIMESTAMPDIFF*")]
    [InlineData("LOWER(x) = 'a'", "*LOWER*")]
    [InlineData("x = ", "*")]
    [InlineData("x = 'unterminated", "*")]
    [InlineData("99999999999999999999 > 0", "*")]
    public void Rejects_what_the_standard_and_Trino_reject(string define, string message)
    {
        var act = () => Define(define);
        act.Should().Throw<PatternValidationException>().WithMessage(message);
    }

    [Theory]
    [InlineData("FINAL A.x")]
    [InlineData("FINAL PREV(LAST(x))")]
    [InlineData("FINAL CLASSIFIER()")]
    [InlineData("FINAL MATCH_NUMBER()")]
    public void Rejects_a_semantics_prefix_on_anything_but_first_last_and_aggregates(string measure)
    {
        var act = () => Measure(measure);
        act.Should().Throw<PatternValidationException>();
    }

    [Fact]
    public void Running_and_final_are_columns_unless_they_prefix_a_call()
    {
        Define("final > 0").Should().BeOfType<BinaryExpr>().Which.Left.Should().Be(new ColumnExpr("final", Nav([])));
        Define("A.final > 0").Should().BeOfType<BinaryExpr>().Which.Left.Should().Be(new ColumnExpr("final", Nav([0])));
        Measure("running + 1").Should().BeOfType<BinaryExpr>().Which.Left.Should().Be(new ColumnExpr("running", Nav([])));
        Define("final AND (x > 0)").Should().BeOfType<BinaryExpr>().Which.Left.Should().Be(new ColumnExpr("final", Nav([])));
        Define("running IN (1, 2)").Should().BeOfType<InExpr>().Which.Operand.Should().Be(new ColumnExpr("running", Nav([])));
        Measure("CASE running WHEN (1) THEN 'a' END").Should().BeOfType<CaseExpr>().Which.Operand.Should().Be(new ColumnExpr("running", Nav([])));
    }

    [Fact]
    public void Accepts_running_and_match_number_inside_define()
    {
        Define("RUNNING LAST(A.x) > 0 AND MATCH_NUMBER() = 1").Should().BeOfType<BinaryExpr>();
        Define("RUNNING COUNT(A.*) < 3").Should().BeOfType<BinaryExpr>();
    }

    [Fact]
    public void A_navigation_resolves_positions_as_Trino_does()
    {
        // Match starts at partition row 2; labels A B A B (A=0, B=1); current row 4 (the second A).
        var labels = new ArrayView([0, 1, 0, 1], 4);

        Nav([0]).ResolvePosition(4, labels, 0, 10, 2).Should().Be(4);                     // LAST(A) running
        Nav([0], logical: 1).ResolvePosition(4, labels, 0, 10, 2).Should().Be(2);         // LAST(A, 1)
        Nav([0], logical: 2).ResolvePosition(4, labels, 0, 10, 2).Should().Be(-1);        // LAST(A, 2): none
        Nav([1], last: false).ResolvePosition(4, labels, 0, 10, 2).Should().Be(3);        // FIRST(B)
        Nav([1], running: false).ResolvePosition(4, labels, 0, 10, 2).Should().Be(5);     // FINAL LAST(B)
        Nav([0], physical: -3).ResolvePosition(4, labels, 0, 10, 2).Should().Be(1);       // PREV(A.x, 3): before the match, inside the partition
        Nav([0], physical: -5).ResolvePosition(4, labels, 0, 10, 2).Should().Be(-1);      // before the partition
        Nav([], physical: 6).ResolvePosition(4, labels, 0, 10, 2).Should().Be(-1);        // NEXT past the partition end
    }
}
