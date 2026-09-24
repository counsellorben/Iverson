using FluentAssertions;
using Iverson.Patterns.Expressions;
using Xunit;
using static Iverson.Patterns.Tests.TestRows;

namespace Iverson.Patterns.Tests;

public sealed class ExpressionEvaluatorTests
{
    // Variables A=0, B=1; subset U = {A, B}.
    private static ExpressionParser Parser() =>
        new(new Dictionary<string, int> { ["A"] = 0, ["B"] = 1 },
            new Dictionary<string, int[]> { ["U"] = [0, 1] },
            new SimilarityTermTable());

    private static EvaluationContext Ctx(List<IDictionary<string, object?>> rows, int[] labels, int patternStart = 0,
        int? currentRow = null, long matchNumber = 1, Func<int, int, double?>? similarity = null) =>
        new()
        {
            Rows = rows,
            LabelNames = ["A", "B"],
            Similarity = similarity ?? ((_, _) => null),
            PatternStart = patternStart,
            CurrentRow = currentRow ?? patternStart + labels.Length - 1,
            MatchedLabels = new ArrayView(labels, labels.Length),
            MatchNumber = matchNumber,
        };

    private static object? Eval(string measure, EvaluationContext ctx) =>
        ExpressionEvaluator.Evaluate(Parser().ParseMeasure(measure), ctx);

    // One row: s = NaN, n = NULL, i = 5, m = long.MinValue.
    private static EvaluationContext NaNRow() =>
        Ctx(Rows(["s", "n", "i", "m"], [double.NaN, null, 5L, long.MinValue]), [0]);

    [Theory]
    [InlineData("s = 0.5", false)]
    [InlineData("s <> 0.5", true)]
    [InlineData("s < 1", false)]
    [InlineData("s >= 0", false)]
    [InlineData("s = s", false)]
    [InlineData("NOT (s = 0.5)", true)]
    [InlineData("s IN (0.5, 1.5)", false)]
    [InlineData("s NOT IN (0.5)", true)]
    [InlineData("s BETWEEN 0 AND 1", false)]
    [InlineData("s NOT BETWEEN 0 AND 1", true)]
    [InlineData("s = n", null)]
    [InlineData("s IN (0.5, n)", null)]
    [InlineData("s BETWEEN 0 AND n", false)]
    [InlineData("s IS NULL", false)]
    [InlineData("s IS NOT NULL", true)]
    [InlineData("i = 5", true)]
    [InlineData("i = 5.0", true)]
    [InlineData("n = n", null)]
    public void NaN_and_null_comparisons_follow_spec_section_2(string expression, bool? expected) =>
        Eval(expression, NaNRow()).Should().Be(expected);

    [Theory]
    [InlineData("s + 1")]
    [InlineData("ROUND(s)")]
    [InlineData("ABS(s)")]
    [InlineData("COALESCE(s, 1)")]
    [InlineData("NULLIF(s, s)")]
    public void NaN_propagates_through_arithmetic_rounding_and_null_functions(string expression) =>
        Eval(expression, NaNRow()).Should().BeOfType<double>().Which.Should().Be(double.NaN);

    [Fact]
    public void Null_functions_treat_null_as_absent()
    {
        Eval("COALESCE(n, i)", NaNRow()).Should().Be(5L);
        Eval("NULLIF(i, 5)", NaNRow()).Should().BeNull();
    }

    [Fact]
    public void Aggregates_follow_the_NaN_rules()
    {
        var rows = Rows(["v"], [1.0], [double.NaN], [2.0]);
        var ctx = Ctx(rows, [0, 0, 0]);

        Eval("FINAL SUM(A.v)", ctx).Should().Be(double.NaN);
        Eval("FINAL AVG(A.v)", ctx).Should().Be(double.NaN);
        Eval("FINAL COUNT(A.v)", ctx).Should().Be(3L);
        Eval("FINAL MIN(A.v)", ctx).Should().Be(double.NaN);
        Eval("FINAL MAX(A.v)", ctx).Should().Be(2.0);
        Eval("FINAL MAX(A.v)", Ctx(Rows(["v"], [double.NaN], [double.NaN]), [0, 0])).Should().Be(double.NaN);
    }

    [Theory]
    [InlineData("n AND FALSE", false)]
    [InlineData("n AND TRUE", null)]
    [InlineData("n OR TRUE", true)]
    [InlineData("n OR FALSE", null)]
    [InlineData("NOT n", null)]
    public void Boolean_logic_is_three_valued(string expression, bool? expected) =>
        Eval(expression, NaNRow()).Should().Be(expected);

    [Theory]
    [InlineData("7 / 2", 3L)]
    [InlineData("-7 / 2", -3L)]
    [InlineData("-7 % 3", -1L)]
    [InlineData("i * 2 - 1", 9L)]
    [InlineData("ABS(-3)", 3L)]
    [InlineData("ROUND(7)", 7L)]
    public void Integer_arithmetic_truncates_toward_zero(string expression, long expected) =>
        Eval(expression, NaNRow()).Should().Be(expected);

    [Theory]
    [InlineData("7 / 2.0", 3.5)]
    [InlineData("1 + 2.5", 3.5)]
    [InlineData("ROUND(2.5)", 3.0)]
    [InlineData("ROUND(-2.5)", -3.0)]
    [InlineData("ROUND(0.125, 2)", 0.13)]
    [InlineData("1.0 / 0", double.PositiveInfinity)]
    [InlineData("0.0 / 0", double.NaN)]
    public void Mixed_and_double_arithmetic_promotes_to_double(string expression, double expected) =>
        Eval(expression, NaNRow()).Should().Be(expected);

    [Theory]
    [InlineData("1 / 0")]
    [InlineData("1 % 0")]
    [InlineData("9223372036854775807 + 1")]
    [InlineData("ABS(m)")]
    [InlineData("-m")]
    [InlineData("'a' + 1")]
    [InlineData("'a' < 1")]
    [InlineData("TIMESTAMPDIFF(SECOND, i, i)")]
    [InlineData("missing")]
    public void Run_time_errors_raise_PatternEvaluationException(string expression)
    {
        var act = () => Eval(expression, NaNRow());
        act.Should().Throw<PatternEvaluationException>();
    }

    [Fact]
    public void Strings_compare_ordinally_and_values_normalize_on_read()
    {
        Eval("'a' < 'B'", NaNRow()).Should().Be(false);
        Eval("'B' < 'a'", NaNRow()).Should().Be(true);
        Eval("v", Ctx(Rows(["v"], [7]), [0])).Should().Be(7L);          // Int32 reads as long
        Eval("v", Ctx(Rows(["v"], [1.5f]), [0])).Should().Be(1.5);      // Single reads as double
        var bytes = () => Eval("v", Ctx(Rows(["v"], [new byte[] { 1 }]), [0]));
        bytes.Should().Throw<PatternEvaluationException>();
    }

    [Fact]
    public void Case_expressions_never_match_null()
    {
        Eval("CASE WHEN i > 3 THEN 'big' ELSE 'small' END", NaNRow()).Should().Be("big");
        Eval("CASE i WHEN 5 THEN 'five' END", NaNRow()).Should().Be("five");
        Eval("CASE n WHEN n THEN 1 ELSE 2 END", NaNRow()).Should().Be(2L);
        Eval("CASE WHEN n THEN 1 END", NaNRow()).Should().BeNull();
    }

    [Fact]
    public void Timestampdiff_counts_whole_units_truncated_toward_zero()
    {
        var ctx = Ctx(Rows(["t1", "t2", "t3"],
            [new DateTime(2026, 1, 1, 10, 0, 0), new DateTime(2026, 1, 1, 9, 58, 30), new DateTime(2026, 1, 3, 9, 0, 0)]), [0]);

        Eval("TIMESTAMPDIFF(SECOND, t1, t2)", ctx).Should().Be(-90L);
        Eval("TIMESTAMPDIFF(MINUTE, t1, t2)", ctx).Should().Be(-1L);
        Eval("TIMESTAMPDIFF(HOUR, t1, t3)", ctx).Should().Be(47L);
        Eval("TIMESTAMPDIFF(DAY, t1, t3)", ctx).Should().Be(1L);
    }

    // Five partition rows x = 10..50; the match starts at row 1 with labels A B B (rows 1–3).
    private static EvaluationContext Match(int currentRow, long matchNumber = 7, Func<int, int, double?>? similarity = null) =>
        Ctx(Rows(["x", "title"], [10L, "t0"], [20L, "t1"], [30L, "t2"], [40L, "t3"], [50L, "t4"]),
            [0, 1, 1], patternStart: 1, currentRow: currentRow, matchNumber: matchNumber, similarity: similarity);

    [Theory]
    [InlineData("PREV(A.x)", 10L)]          // before the match, inside the partition
    [InlineData("PREV(A.x, 2)", null)]      // before the partition
    [InlineData("NEXT(B.x)", 50L)]          // after the match
    [InlineData("NEXT(B.x, 2)", null)]      // after the partition
    [InlineData("FIRST(B.x)", 30L)]
    [InlineData("FIRST(B.x, 1)", 40L)]
    [InlineData("FIRST(B.x, 2)", null)]
    [InlineData("x", 40L)]
    [InlineData("CLASSIFIER()", "B")]
    [InlineData("PREV(CLASSIFIER(), 2)", "A")]
    [InlineData("PREV(CLASSIFIER(), 3)", null)] // outside the match
    [InlineData("MATCH_NUMBER()", 7L)]
    [InlineData("COUNT(*)", 3L)]
    [InlineData("SUM(B.x)", 70L)]
    [InlineData("MIN(B.x)", 30L)]
    [InlineData("AVG(U.x)", 30.0)]
    [InlineData("COUNT(CLASSIFIER())", 3L)]
    public void Measures_read_through_navigations_at_the_last_row(string measure, object? expected) =>
        Eval(measure, Match(currentRow: 3)).Should().Be(expected);

    [Fact]
    public void Running_reads_stop_at_the_current_row_and_final_reads_see_the_whole_match()
    {
        Eval("COUNT(B.*)", Match(currentRow: 2)).Should().Be(1L);
        Eval("FINAL COUNT(B.*)", Match(currentRow: 2)).Should().Be(2L);
        Eval("LAST(x)", Match(currentRow: 2)).Should().Be(30L);
        Eval("FINAL LAST(x)", Match(currentRow: 2)).Should().Be(40L);
        Eval("CLASSIFIER(B)", Match(currentRow: 1)).Should().BeNull();       // no B yet under RUNNING
    }

    [Fact]
    public void Similarity_reads_the_score_at_the_navigated_row()
    {
        Func<int, int, double?> scores = (row, term) => term == 0 && row == 3 ? 0.8 : null;

        Eval("SIMILARITY(title, 'q')", Match(3, similarity: scores)).Should().Be(0.8);
        Eval("PREV(SIMILARITY(title, 'q'))", Match(3, similarity: scores)).Should().BeNull();
    }

    [Fact]
    public void An_empty_match_reads_null_except_match_number_and_count()
    {
        var ctx = Match(currentRow: 1, matchNumber: 4);
        ctx.EmptyMatch = true;
        ctx.MatchedLabels = ArrayView.Empty;

        Eval("MATCH_NUMBER()", ctx).Should().Be(4L);
        Eval("CLASSIFIER()", ctx).Should().BeNull();
        Eval("x", ctx).Should().BeNull();
        Eval("COUNT(*)", ctx).Should().Be(0L);
        Eval("SUM(A.x)", ctx).Should().BeNull();
    }

    [Fact]
    public void A_define_labels_the_row_only_when_true()
    {
        var parser = Parser();

        ExpressionEvaluator.EvaluateDefine(parser.ParseDefine("B.x > PREV(B.x)"), Match(3)).Should().BeTrue();
        ExpressionEvaluator.EvaluateDefine(parser.ParseDefine("B.x < PREV(B.x)"), Match(3)).Should().BeFalse();
        ExpressionEvaluator.EvaluateDefine(parser.ParseDefine("PREV(B.x, 5) > 0"), Match(3)).Should().BeFalse(); // NULL

        var nonBoolean = () => ExpressionEvaluator.EvaluateDefine(parser.ParseDefine("COALESCE(x, 0)"), Match(3));
        nonBoolean.Should().Throw<PatternEvaluationException>().WithMessage("*boolean*");
    }
}
