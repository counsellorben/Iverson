using System.Diagnostics;
using FluentAssertions;
using Xunit;
using static Iverson.Patterns.Tests.TestRows;

namespace Iverson.Patterns.Tests;

public sealed class PatternQueryTests
{
    private static readonly List<IDictionary<string, object?>> D1 = Rows(["id", "value"],
        [1L, 90L], [2L, 80L], [3L, 70L], [4L, 80L], [5L, 90L], [6L, 50L], [7L, 40L], [8L, 60L]);

    private static readonly List<IDictionary<string, object?>> D2 = Rows(["id", "value"],
        [1L, 90L], [2L, 80L], [3L, 70L], [4L, 70L]);

    private static readonly (string, string)[] MS =
        [("match", "MATCH_NUMBER()"), ("val", "RUNNING LAST(value)"), ("label", "CLASSIFIER()")];
    private static readonly (string, string) BD = ("B", "B.value < PREV(B.value)");
    private static readonly (string, string) CD = ("C", "C.value > PREV(C.value)");

    private static PatternRequest Req(string pattern, (string Name, string Expr)[]? define = null,
        (string Name, string Expr)[]? measures = null, RowsPerMatch rows = RowsPerMatch.AllRowsShowEmpty,
        AfterMatchSkipKind skip = AfterMatchSkipKind.PastLastRow, string skipVariable = "",
        string[]? partitionBy = null, SubsetDefinition[]? subsets = null,
        PatternSource source = PatternSource.TypeRows, string? tenant = "TenantId") =>
        new(source, partitionBy ?? [], pattern, subsets ?? [],
            (define ?? []).Select(d => new NamedExpression(d.Name, d.Expr)).ToList(),
            (measures ?? []).Select(m => new NamedExpression(m.Name, m.Expr)).ToList(),
            rows, skip, skipVariable, tenant);

    private static List<MatchOutputRow> Run(PatternRequest request, List<IDictionary<string, object?>> rows,
        Func<int, int, double?>? similarity = null, PatternBudget? budget = null) =>
        PatternQuery.Compile(request, 5000)
            .Run(rows, similarity ?? ((_, _) => null), budget ?? new PatternBudget(10_000, 10_000_000))
            .ToList();

    private static MatchOutputRow Out(long matchNumber, string classifier, params (string Key, object? Value)[] data) =>
        new(data.ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal), matchNumber, classifier);

    /// <summary>An ALL_ROWS_* row with the MS measures.</summary>
    private static MatchOutputRow AllRow(long id, long value, long matchNumber, string classifier,
        object? match, object? val, object? label) =>
        Out(matchNumber, classifier, ("id", id), ("value", value), ("match", match), ("val", val), ("label", label));

    /// <summary>A matched ALL_ROWS_* row with the MS measures, where RUNNING LAST(value) is the row's own value.</summary>
    private static MatchOutputRow Matched(long id, long value, long matchNumber, string label) =>
        AllRow(id, value, matchNumber, label, matchNumber, value, label);

    private static void ShouldMatch(List<MatchOutputRow> actual, params MatchOutputRow[] expected) =>
        actual.Should().BeEquivalentTo(expected, o => o.WithStrictOrdering().ComparingByMembers<MatchOutputRow>());

    // ---- Run: expectations from Trino 483 ----

    [Fact]
    public void All_rows_per_match_with_prev_navigation()
    {
        ShouldMatch(Run(Req("A B+ C+", [BD, CD], MS), D1),
            Matched(1, 90, 1, "A"), Matched(2, 80, 1, "B"), Matched(3, 70, 1, "B"), Matched(4, 80, 1, "C"),
            Matched(5, 90, 1, "C"), Matched(6, 50, 2, "A"), Matched(7, 40, 2, "B"), Matched(8, 60, 2, "C"));
    }

    [Fact]
    public void One_row_per_match_shows_empty_matches()
    {
        ShouldMatch(Run(Req("B*", [BD], MS, RowsPerMatch.OneRow), D2),
            Out(1, "", ("match", 1L), ("val", null), ("label", null)),
            Out(2, "", ("match", 2L), ("val", 70L), ("label", "B")),
            Out(3, "", ("match", 3L), ("val", null), ("label", null)));

        ShouldMatch(Run(Req("B+", [BD], MS, RowsPerMatch.OneRow), D2),
            Out(1, "", ("match", 1L), ("val", 70L), ("label", "B")));
    }

    [Fact]
    public void All_rows_modes_show_omit_and_unmatched()
    {
        ShouldMatch(Run(Req("B*", [BD], MS, RowsPerMatch.AllRowsShowEmpty), D2),
            AllRow(1, 90, 1, "", 1L, null, null), Matched(2, 80, 2, "B"), Matched(3, 70, 2, "B"),
            AllRow(4, 70, 3, "", 3L, null, null));

        ShouldMatch(Run(Req("B*", [BD], MS, RowsPerMatch.AllRowsOmitEmpty), D2),
            Matched(2, 80, 2, "B"), Matched(3, 70, 2, "B"));

        ShouldMatch(Run(Req("B+", [BD], MS, RowsPerMatch.AllRowsWithUnmatched), D2),
            AllRow(1, 90, 0, "", null, null, null), Matched(2, 80, 1, "B"), Matched(3, 70, 1, "B"),
            AllRow(4, 70, 0, "", null, null, null));
    }

    private static readonly MatchOutputRow[] PastLast =
    [
        Matched(1, 90, 1, "A"), Matched(2, 80, 1, "B"), Matched(3, 70, 1, "B"),
        Matched(5, 90, 2, "A"), Matched(6, 50, 2, "B"), Matched(7, 40, 2, "B"),
    ];

    private static readonly MatchOutputRow[] NextRow =
    [
        Matched(1, 90, 1, "A"), Matched(2, 80, 1, "B"), Matched(3, 70, 1, "B"), Matched(2, 80, 2, "A"),
        Matched(3, 70, 2, "B"), Matched(5, 90, 3, "A"), Matched(6, 50, 3, "B"), Matched(7, 40, 3, "B"),
        Matched(6, 50, 4, "A"), Matched(7, 40, 4, "B"),
    ];

    [Theory]
    [InlineData(AfterMatchSkipKind.PastLastRow, "", false)]
    [InlineData(AfterMatchSkipKind.ToNextRow, "", true)]
    [InlineData(AfterMatchSkipKind.ToFirst, "b", true)]
    [InlineData(AfterMatchSkipKind.ToLast, "B", false)]
    public void After_match_skip_modes(AfterMatchSkipKind skip, string variable, bool overlapping)
    {
        var actual = Run(Req("A B+ | C", [BD, ("C", "FALSE")], MS, skip: skip, skipVariable: variable), D1);
        ShouldMatch(actual, overlapping ? NextRow : PastLast);
    }

    [Fact]
    public void Unmatched_rows_inside_an_earlier_match_are_not_output()
    {
        // Trino 483 (final-fix-report.md, query 7a). SKIP TO NEXT ROW retries at every row; the attempts at ids 3
        // and 7 fail but lie inside the matches that started at ids 2 and 6, so they are not output as unmatched
        // rows. The attempts at ids 4 and 8 fail past every match so far and are.
        ShouldMatch(Run(Req("A B+ | C", [BD, ("C", "FALSE")], MS, RowsPerMatch.AllRowsWithUnmatched,
                AfterMatchSkipKind.ToNextRow), D1),
            Matched(1, 90, 1, "A"), Matched(2, 80, 1, "B"), Matched(3, 70, 1, "B"),
            Matched(2, 80, 2, "A"), Matched(3, 70, 2, "B"),
            AllRow(4, 80, 0, "", null, null, null),
            Matched(5, 90, 3, "A"), Matched(6, 50, 3, "B"), Matched(7, 40, 3, "B"),
            Matched(6, 50, 4, "A"), Matched(7, 40, 4, "B"),
            AllRow(8, 60, 0, "", null, null, null));
    }

    [Fact]
    public void Skip_to_first_of_a_subset_listed_out_of_order()
    {
        // Trino 483 (final-fix-report.md, query 7b). U = (C, A) holds labels 3 and 1; FIRST(U) is each match's A,
        // the row after X, so every match restarts one row later: five overlapping matches.
        ShouldMatch(Run(Req("X A B C", [("X", "TRUE")], MS, skip: AfterMatchSkipKind.ToFirst, skipVariable: "U",
                subsets: [new SubsetDefinition("U", ["C", "A"])]), D1),
            Matched(1, 90, 1, "X"), Matched(2, 80, 1, "A"), Matched(3, 70, 1, "B"), Matched(4, 80, 1, "C"),
            Matched(2, 80, 2, "X"), Matched(3, 70, 2, "A"), Matched(4, 80, 2, "B"), Matched(5, 90, 2, "C"),
            Matched(3, 70, 3, "X"), Matched(4, 80, 3, "A"), Matched(5, 90, 3, "B"), Matched(6, 50, 3, "C"),
            Matched(4, 80, 4, "X"), Matched(5, 90, 4, "A"), Matched(6, 50, 4, "B"), Matched(7, 40, 4, "C"),
            Matched(5, 90, 5, "X"), Matched(6, 50, 5, "A"), Matched(7, 40, 5, "B"), Matched(8, 60, 5, "C"));
    }

    [Theory]
    [InlineData(AfterMatchSkipKind.ToFirst, "A", "*cannot skip to first row of match*")]
    [InlineData(AfterMatchSkipKind.ToLast, "C", "*not present in match*")]
    public void An_illegal_skip_fails_when_a_match_is_found(AfterMatchSkipKind skip, string variable, string message)
    {
        var act = () => Run(Req("A B+ | C", [BD, ("C", "FALSE")], MS, skip: skip, skipVariable: variable), D1);
        act.Should().Throw<PatternEvaluationException>().WithMessage(message);
    }

    [Fact]
    public void Excluded_rows_are_omitted_from_all_rows_output()
    {
        ShouldMatch(Run(Req("A {- B+ -} C+", [BD, CD], MS), D1),
            Matched(1, 90, 1, "A"), Matched(4, 80, 1, "C"), Matched(5, 90, 1, "C"),
            Matched(6, 50, 2, "A"), Matched(8, 60, 2, "C"));
    }

    [Fact]
    public void Running_and_final_measures()
    {
        MatchOutputRow Row(long id, long value, long mn, string cls, long fl, long fc, long rc) =>
            Out(mn, cls, ("id", id), ("value", value), ("fl", fl), ("fc", fc), ("rc", rc));

        ShouldMatch(Run(Req("A B+ C+", [BD, CD],
                [("fl", "FINAL LAST(value)"), ("fc", "FINAL COUNT(*)"), ("rc", "RUNNING COUNT(*)")]), D1),
            Row(1, 90, 1, "A", 90, 5, 1), Row(2, 80, 1, "B", 90, 5, 2), Row(3, 70, 1, "B", 90, 5, 3),
            Row(4, 80, 1, "C", 90, 5, 4), Row(5, 90, 1, "C", 90, 5, 5),
            Row(6, 50, 2, "A", 60, 3, 1), Row(7, 40, 2, "B", 60, 3, 2), Row(8, 60, 2, "C", 60, 3, 3));
    }

    [Fact]
    public void Match_number_inside_define_is_the_number_the_attempt_will_receive()
    {
        ShouldMatch(Run(Req("A", [("A", "MATCH_NUMBER() <= 2")], MS), D2),
            Matched(1, 90, 1, "A"), Matched(2, 80, 2, "A"));

        ShouldMatch(Run(Req("A?", [("A", "MATCH_NUMBER() = 2")], MS, RowsPerMatch.OneRow), D2),
            Out(1, "", ("match", 1L), ("val", null), ("label", null)),
            Out(2, "", ("match", 2L), ("val", 80L), ("label", "A")),
            Out(3, "", ("match", 3L), ("val", null), ("label", null)),
            Out(4, "", ("match", 4L), ("val", null), ("label", null)));
    }

    [Fact]
    public void Subsets_and_navigation_measures()
    {
        ShouldMatch(Run(Req("A B+ C+", [BD, CD], [("s", "FIRST(U.value) + LAST(U.value)"), ("c", "COUNT(U.*)")],
                RowsPerMatch.OneRow, subsets: [new SubsetDefinition("U", ["B", "C"])]), D1),
            Out(1, "", ("s", 170L), ("c", 4L)), Out(2, "", ("s", 100L), ("c", 2L)));

        ShouldMatch(Run(Req("A B+ C+", [BD, CD],
                [("n", "PREV(FIRST(B.value), 1) + NEXT(LAST(B.value), 1)"), ("f1", "FIRST(B.value, 1)"), ("l1", "LAST(C.value, 1)")],
                RowsPerMatch.OneRow), D1),
            Out(1, "", ("n", 170L), ("f1", 70L), ("l1", 80L)), Out(2, "", ("n", 110L), ("f1", null), ("l1", null)));
    }

    [Fact]
    public void Variable_names_are_case_insensitive_and_classified_in_upper_case()
    {
        ShouldMatch(Run(Req("a b+", [("b", "B.value < PREV(b.value)")], MS), D1),
            Matched(1, 90, 1, "A"), Matched(2, 80, 1, "B"), Matched(3, 70, 1, "B"),
            Matched(5, 90, 2, "A"), Matched(6, 50, 2, "B"), Matched(7, 40, 2, "B"));
    }

    [Fact]
    public void Define_may_read_a_following_row()
    {
        ShouldMatch(Run(Req("A B", [("A", "NEXT(A.value) < A.value")], MS), D1),
            Matched(1, 90, 1, "A"), Matched(2, 80, 1, "B"), Matched(5, 90, 2, "A"), Matched(6, 50, 2, "B"));
    }

    [Fact]
    public void Anchors_permute_and_reluctant_quantifiers()
    {
        ShouldMatch(Run(Req("^ A", [("A", "TRUE")], MS), D2), Matched(1, 90, 1, "A"));

        ShouldMatch(Run(Req("PERMUTE(A, B)", [("A", "A.value = 80"), ("B", "B.value = 90")], MS), D2),
            Matched(1, 90, 1, "B"), Matched(2, 80, 1, "A"));

        ShouldMatch(Run(Req("A B+?", [BD], MS), D1),
            Matched(1, 90, 1, "A"), Matched(2, 80, 1, "B"), Matched(5, 90, 2, "A"), Matched(6, 50, 2, "B"));
    }

    [Fact]
    public void History_dependent_defines_are_not_pruned_away()
    {
        // Trino's testExponentialMatch: the match B B B B B B B B LAST is the last of over 2^9 candidates.
        var rows = Rows(["value"], [1L], [2L], [3L], [4L], [5L], [6L], [7L], [8L], [9L]);
        var define = "FIRST(CLASSIFIER()) = 'B' AND FIRST(CLASSIFIER(), 1) = 'B' AND FIRST(CLASSIFIER(), 2) = 'B' AND " +
                     "FIRST(CLASSIFIER(), 3) = 'B' AND FIRST(CLASSIFIER(), 4) = 'B' AND FIRST(CLASSIFIER(), 5) = 'B' AND " +
                     "FIRST(CLASSIFIER(), 6) = 'B' AND FIRST(CLASSIFIER(), 7) = 'B'";

        var output = Run(Req("(A | B)+ LAST", [("LAST", define)], [("classy", "CLASSIFIER()")]), rows);

        output.Select(r => r.Data["classy"]).Should().Equal("B", "B", "B", "B", "B", "B", "B", "B", "LAST");
    }

    [Fact]
    public void One_row_output_names_partition_columns_as_the_row_spells_them()
    {
        var partition = Rows(["Part", "id", "value"], ["x", 1L, 10L], ["x", 2L, 20L]);

        ShouldMatch(Run(Req("A", measures: [("v", "A.value")], rows: RowsPerMatch.OneRow, partitionBy: ["part"]), partition),
            Out(1, "", ("Part", "x"), ("v", 10L)), Out(2, "", ("Part", "x"), ("v", 20L)));
    }

    [Fact]
    public void Chunks_one_row_output_starts_with_parent_key()
    {
        var chunks = Rows(["parent_key", "chunk_index", "text"], ["doc1", 0L, "intro"], ["doc1", 1L, "refund"]);

        ShouldMatch(Run(Req("A B", [("B", "B.text = 'refund'")], [("n", "COUNT(*)")], RowsPerMatch.OneRow,
                source: PatternSource.Chunks), chunks),
            Out(1, "", ("parent_key", "doc1"), ("n", 2L)));
    }

    [Fact]
    public void Similarity_scores_reach_defines_and_measures_by_row_and_term()
    {
        var rows = Rows(["id", "title"], [1L, "a"], [2L, "b"], [3L, "c"]);
        Func<int, int, double?> scores = (row, term) => term == 0 ? (row == 1 ? 0.9 : 0.1) : null;

        ShouldMatch(Run(Req("A", [("A", "SIMILARITY(title, 'refund') > 0.5")], [("s", "SIMILARITY(title, 'refund')")],
                RowsPerMatch.OneRow), rows, scores),
            Out(1, "", ("s", 0.9)));
    }

    [Fact]
    public void Run_time_errors_and_budgets_surface_during_enumeration()
    {
        var divide = () => Run(Req("A", [("A", "A.value / 0 > 1")]), D2);
        divide.Should().Throw<PatternEvaluationException>();

        var threads = () => Run(Req("A | B"), D2, budget: new PatternBudget(maxActiveThreads: 1, maxSteps: 1_000));
        threads.Should().Throw<PatternBudgetExceededException>().Which.BudgetName.Should().Be("MaxActiveThreads");
    }

    [Theory]
    [InlineData("LAST(A.value, 2000000000) IS NULL")]
    [InlineData("PREV(CLASSIFIER(A), 2000000000) IS NULL")]
    public void Huge_navigation_offsets_compile_and_run_in_bounded_time(string read)
    {
        // C's define is reachable from every instruction of the loop, so every thread comparison walks the read's
        // offsets. C never holds (every value is positive), so nothing matches. Before the bound, Compile alone
        // materialized one navigation per offset: about 200 GB at this offset.
        var sw = Stopwatch.StartNew();
        var compiled = PatternQuery.Compile(Req("(A | B)+ C", [("C", $"{read} AND C.value < 0")], MS), 5000);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "Compile must not expand the offsets");

        sw.Restart();
        compiled.Run(D2, (_, _) => null, new PatternBudget(10_000, 10_000_000)).Should().BeEmpty();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "the comparison walks only the matched labels");
    }

    [Fact]
    public void One_label_over_one_row_costs_three_steps_through_the_partition_loop()
    {
        // The three matcher steps of MatcherTests.One_label_over_one_true_row_costs_three_steps; the partition
        // loop adds none.
        var budget = new PatternBudget(10_000, 10_000_000);
        Run(Req("A", [("A", "TRUE")], MS), Rows(["id", "value"], [1L, 90L]), budget: budget)
            .Should().ContainSingle();
        budget.StepsUsed.Should().Be(3);
    }

    [Fact]
    public void A_cancelled_budget_token_stops_the_run()
    {
        var budget = new PatternBudget(10_000, 10_000_000, new CancellationToken(canceled: true));

        var act = () => Run(Req("A", [("A", "TRUE")], MS), D2, budget: budget);

        act.Should().Throw<OperationCanceledException>();
        budget.StepsUsed.Should().Be(0);
    }

    [Fact]
    public void Cancelling_the_budget_token_interrupts_a_long_run()
    {
        // Each define sums the whole match so far, so 300,000 steps take over ten seconds (2,000,000 took 90 s):
        // a run that ignored the token would end in PatternBudgetExceededException instead, not hang the suite.
        var rows = Rows(["id", "x"], Enumerable.Range(0, 3000).Select(i => new object?[] { (long)i, (long)(i % 7) }).ToArray());
        var compiled = PatternQuery.Compile(Req("A+ B", [("A", "SUM(A.x) >= 0"), ("B", "FALSE")]), 5000);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var budget = new PatternBudget(10_000, 300_000, cancellation.Token);

        var sw = Stopwatch.StartNew();
        var act = () => compiled.Run(rows, (_, _) => null, budget).ToList();

        act.Should().Throw<OperationCanceledException>();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        budget.StepsUsed.Should().BePositive("the run was under way when the token was cancelled");
    }

    // ---- Compile: validation (spec §1, §2) ----

    private static void Rejects(PatternRequest request, string message = "*")
    {
        var act = () => PatternQuery.Compile(request, 5000);
        act.Should().Throw<PatternValidationException>().WithMessage(message);
    }

    [Fact]
    public void Define_entries_must_name_distinct_pattern_variables()
    {
        Rejects(Req("A B", [("B", "TRUE"), ("b", "FALSE")]), "*twice*");
        Rejects(Req("A B", [("C", "TRUE")]), "*not a primary pattern variable*");
    }

    [Fact]
    public void Measure_names_must_be_distinct_ordinally_and_not_the_tenant_column()
    {
        Rejects(Req("A", measures: [("m", "1"), ("m", "2")]), "*m*");
        PatternQuery.Compile(Req("A", measures: [("m", "1"), ("M", "2")]), 5000).MeasureNames.Should().Equal("m", "M");
        Rejects(Req("A", measures: [("tenantid", "1")], tenant: "TenantId"), "*tenantid*");
        PatternQuery.Compile(Req("A", measures: [("tenantid", "1")], tenant: null), 5000).Should().NotBeNull();
    }

    [Fact]
    public void Chunks_requests_are_checked_against_the_chunk_columns()
    {
        Rejects(Req("A", partitionBy: ["x"], source: PatternSource.Chunks));
        Rejects(Req("A", measures: [("parent_key", "1")], rows: RowsPerMatch.OneRow, source: PatternSource.Chunks), "*parent_key*");
        PatternQuery.Compile(Req("A", measures: [("text", "1")], rows: RowsPerMatch.OneRow, source: PatternSource.Chunks), 5000);
        Rejects(Req("A", measures: [("text", "1")], rows: RowsPerMatch.AllRowsShowEmpty, source: PatternSource.Chunks), "*text*");
        PatternQuery.Compile(Req("A", measures: [("Parent_Key", "1")], rows: RowsPerMatch.OneRow, source: PatternSource.Chunks), 5000);
        Rejects(Req("A", [("A", "A.title = 'x'")], source: PatternSource.Chunks), "*title*");
        PatternQuery.Compile(Req("A", [("A", "A.TEXT = 'x' AND chunk_index > 0")], source: PatternSource.Chunks), 5000);
        Rejects(Req("A", [("A", "SIMILARITY(chunk_index, 'x') > 0")], source: PatternSource.Chunks), "*SIMILARITY*");
        PatternQuery.Compile(Req("A", [("A", "SIMILARITY(TEXT, 'x') > 0")], source: PatternSource.Chunks), 5000);
    }

    [Fact]
    public void Undefined_enum_values_are_rejected_naming_the_field()
    {
        Rejects(Req("A", source: (PatternSource)5), "source: 5 is not a defined PatternSource value.");
        Rejects(Req("A", rows: (RowsPerMatch)9), "rows_per_match: 9 is not a defined RowsPerMatch value.");
        Rejects(Req("A", skip: (AfterMatchSkipKind)7, skipVariable: "A"),
            "after_match: 7 is not a defined AfterMatchSkipKind value.");
    }

    [Theory]
    [InlineData("x", "X")]
    [InlineData("x", "x")]
    public void Partition_by_columns_must_be_distinct_case_insensitively(string first, string second)
    {
        Rejects(Req("A", rows: RowsPerMatch.OneRow, partitionBy: [first, second]),
            $"partition_by column '{second}' is listed more than once.");
        Rejects(Req("A", rows: RowsPerMatch.AllRowsShowEmpty, partitionBy: ["y", first, second]), $"*'{second}'*");
        PatternQuery.Compile(Req("A", rows: RowsPerMatch.OneRow, partitionBy: [first, "y"]), 5000).Should().NotBeNull();
    }

    [Fact]
    public void Exclusion_cannot_combine_with_unmatched_rows() =>
        Rejects(Req("A {- B -}", rows: RowsPerMatch.AllRowsWithUnmatched), "*xclusion*");

    [Fact]
    public void Skip_variables_must_fit_the_skip_mode()
    {
        Rejects(Req("A B", skip: AfterMatchSkipKind.ToFirst, skipVariable: ""));
        Rejects(Req("A B", skip: AfterMatchSkipKind.ToFirst, skipVariable: "C"), "*C*");
        Rejects(Req("A B", skip: AfterMatchSkipKind.PastLastRow, skipVariable: "A"));
        PatternQuery.Compile(Req("A B", skip: AfterMatchSkipKind.ToLast, skipVariable: "u",
            subsets: [new SubsetDefinition("U", ["A", "B"])]), 5000);
    }

    [Fact]
    public void The_program_cap_is_enforced_by_compile() =>
        Rejects(Req("A{5000}"), "*MaxProgramInstructions*");

    [Fact]
    public void Compile_exposes_referenced_columns_similarity_terms_and_measure_names()
    {
        var compiled = PatternQuery.Compile(Req("A B",
            [("A", "SIMILARITY(title, 'refund') > 0.5"), ("B", "B.Price > PREV(B.price)")],
            [("t", "SIMILARITY(Title, 'refund')"), ("q", "SUM(B.qty)")]), 5000);

        compiled.ReferencedColumns.Should().BeEquivalentTo(["title", "Price", "qty"]);
        compiled.ReferencedColumns.Contains("PRICE").Should().BeTrue();
        compiled.SimilarityTerms.Should().Equal(new SimilarityTerm("title", "refund"));
        compiled.MeasureNames.Should().Equal("t", "q");
    }
}
