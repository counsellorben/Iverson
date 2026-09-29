using FluentAssertions;
using Xunit;
using static Iverson.Patterns.Tests.TestRows;

namespace Iverson.Patterns.Tests;

/// <summary>
/// Pins the reading-1 verdict of docs/specs/2026-09-28-matchpattern-rrf-test-design.md ("RRF inside one
/// request"): <c>measures</c> can compute the <c>order_by</c> leg of RRF (<c>1 / (60 + rank)</c>), the reverse rank
/// and a normalised similarity, but not a rank by <c>SIMILARITY</c>. If the last fact starts failing, the engine
/// has gained a correlated count and the verdict must be revisited.
/// </summary>
public sealed class RrfExpressivenessTests
{
    // Five rows, already in order_by order: the engine numbers rows in the order the row source delivers them.
    private static readonly List<IDictionary<string, object?>> D = Rows(["id", "title"],
        [1L, "a"], [2L, "b"], [3L, "c"], [4L, "d"], [5L, "e"]);

    // SIMILARITY(title, 'q') per row; row 2 (index 1) is the maximum.
    private static readonly double[] Scores = [0.3, 0.9, 0.45, 0.6, 0.1];

    /// <summary>One match over every row (<c>A+</c>, <c>A AS TRUE</c>), ALL ROWS PER MATCH, one measure <c>m</c>.</summary>
    private static PatternRequest Req(string measure) =>
        new(PatternSource.TypeRows, [], "A+", [], [new NamedExpression("A", "TRUE")],
            [new NamedExpression("m", measure)], RowsPerMatch.AllRowsShowEmpty, AfterMatchSkipKind.PastLastRow, "",
            "TenantId");

    private static List<object?> Run(string measure, Func<int, int, double?>? similarity = null) =>
        PatternQuery.Compile(Req(measure), 5000)
            .Run(D, similarity ?? ((_, _) => null), new PatternBudget(10_000, 10_000_000))
            .Select(r => r.Data["m"])
            .ToList();

    [Fact]
    public void The_order_by_rrf_term_is_one_over_sixty_plus_the_running_count()
    {
        Run("1.0 / (60 + RUNNING COUNT(*))").Should().Equal(1.0 / 61, 1.0 / 62, 1.0 / 63, 1.0 / 64, 1.0 / 65);
    }

    [Fact]
    public void Final_count_minus_running_count_plus_one_is_the_reverse_rank()
    {
        Run("FINAL COUNT(*) - RUNNING COUNT(*) + 1").Should().Equal(5L, 4L, 3L, 2L, 1L);
    }

    [Fact]
    public void Similarity_over_its_final_max_normalises_the_best_row_to_one()
    {
        var normalised = Run("SIMILARITY(Title, 'q') / FINAL MAX(SIMILARITY(Title, 'q'))", (row, _) => Scores[row]);

        normalised.Should().Equal(Scores.Select(s => (object?)(s / 0.9)));
        normalised[1].Should().Be(1.0);
    }

    [Fact]
    public void A_rank_by_similarity_needs_a_correlated_count_which_compile_rejects()
    {
        var act = () => PatternQuery.Compile(
            Req("FINAL SUM(CASE WHEN SIMILARITY(A.Title,'q') > SIMILARITY(Title,'q') THEN 1 ELSE 0 END) + 1"), 5000);

        act.Should().Throw<PatternValidationException>().WithMessage("*must match*");
    }
}
