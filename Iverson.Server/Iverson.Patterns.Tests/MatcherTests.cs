using FluentAssertions;
using Iverson.Patterns.Compilation;
using Iverson.Patterns.Expressions;
using Iverson.Patterns.Matching;
using Iverson.Patterns.Syntax;
using Xunit;

namespace Iverson.Patterns.Tests;

public sealed class MatcherTests
{
    /// <summary>truth[row][label]: whether the label's define holds at that row. History-free by construction.</summary>
    private sealed class TableEvaluator(bool[][] truth, int patternStart = 0, bool atPartitionStart = true) : ILabelEvaluator
    {
        public int InputLength => truth.Length - patternStart;
        public bool MatchingAtPartitionStart => atPartitionStart;
        public bool EvaluateLabel(ArrayView matchedLabels) =>
            truth[patternStart + matchedLabels.Length - 1][matchedLabels[matchedLabels.Length - 1]];
    }

    private static (Matcher Matcher, IReadOnlyList<string> Labels) Build(string pattern, int maxInstructions = 5000)
    {
        var parsed = PatternParser.Parse(pattern, []);
        var program = ProgramCompilerTests.CompileText(pattern, maxInstructions);
        var equivalence = new ThreadEquivalence(program, new Expr?[parsed.Variables.Count]);
        return (new Matcher(program, equivalence), parsed.Variables);
    }

    private static bool[][] AllTrue(int rows, int labels) =>
        Enumerable.Range(0, rows).Select(_ => Enumerable.Repeat(true, labels).ToArray()).ToArray();

    private static PatternBudget Budget() => new(maxActiveThreads: 10_000, maxSteps: 10_000_000);

    [Fact]
    public void Alternation_prefers_the_left_branch()
    {
        var (matcher, _) = Build("A | B");
        var result = matcher.Run(new TableEvaluator(AllTrue(1, 2)), Budget());

        result.Matched.Should().BeTrue();
        result.Labels.ToArray().Should().Equal(0);
    }

    [Fact]
    public void Greedy_takes_the_longest_and_reluctant_the_shortest_match()
    {
        var (greedy, _) = Build("A*");
        greedy.Run(new TableEvaluator(AllTrue(3, 1)), Budget()).Labels.ToArray().Should().Equal(0, 0, 0);

        var (reluctant, _) = Build("A*?");
        var empty = reluctant.Run(new TableEvaluator(AllTrue(3, 1)), Budget());
        empty.Matched.Should().BeTrue();
        empty.Labels.Length.Should().Be(0);
    }

    [Fact]
    public void Permute_prefers_the_lexicographically_first_ordering()
    {
        var (matcher, _) = Build("PERMUTE(A, B)");
        matcher.Run(new TableEvaluator(AllTrue(2, 2)), Budget()).Labels.ToArray().Should().Equal(0, 1);
    }

    [Fact]
    public void Exclusions_are_reported_as_relative_start_end_pairs()
    {
        var (matcher, _) = Build("A {- B -} C");
        var result = matcher.Run(new TableEvaluator(AllTrue(3, 3)), Budget());

        result.Labels.ToArray().Should().Equal(0, 1, 2);
        result.Exclusions.ToArray().Should().Equal(1, 2);
    }

    [Fact]
    public void Anchors_constrain_the_partition_start_and_end()
    {
        var (start, _) = Build("^ A");
        start.Run(new TableEvaluator(AllTrue(2, 1), patternStart: 1, atPartitionStart: false), Budget())
            .Matched.Should().BeFalse();
        start.Run(new TableEvaluator(AllTrue(2, 1)), Budget()).Matched.Should().BeTrue();

        var (end, _) = Build("A $");
        end.Run(new TableEvaluator(AllTrue(2, 1)), Budget()).Matched.Should().BeFalse();

        var (endPlus, _) = Build("A+ $");
        endPlus.Run(new TableEvaluator(AllTrue(2, 1)), Budget()).Labels.ToArray().Should().Equal(0, 0);
    }

    [Fact]
    public void A_false_first_label_is_no_match()
    {
        var (matcher, _) = Build("A B");
        matcher.Run(new TableEvaluator([[false, true], [true, true]]), Budget()).Matched.Should().BeFalse();
    }

    [Fact]
    public void Equivalent_threads_are_pruned_so_an_exponential_pattern_runs_in_linear_steps()
    {
        // Trino's testPotentiallyExponentialMatch: (A+)+ B over rows where A holds and B never does.
        long Steps(int rows)
        {
            var (matcher, _) = Build("(A+)+ B");
            var truth = Enumerable.Range(0, rows).Select(_ => new[] { true, false }).ToArray();
            var budget = Budget();
            matcher.Run(new TableEvaluator(truth), budget).Matched.Should().BeFalse();
            return budget.StepsUsed;
        }

        var ten = Steps(10);
        var twenty = Steps(20);
        twenty.Should().BeLessThan(3 * ten, "pruning keeps the work linear in the input; unpruned it doubles per row");
    }

    [Fact]
    public void Exceeding_MaxActiveThreads_throws()
    {
        var (matcher, _) = Build("A | B");
        var act = () => matcher.Run(new TableEvaluator(AllTrue(1, 2)), new PatternBudget(maxActiveThreads: 1, maxSteps: 1_000));
        act.Should().Throw<PatternBudgetExceededException>().Which.BudgetName.Should().Be("MaxActiveThreads");
    }

    [Fact]
    public void Exceeding_MaxSteps_throws()
    {
        var (matcher, _) = Build("A+");
        var act = () => matcher.Run(new TableEvaluator(AllTrue(10, 1)), new PatternBudget(maxActiveThreads: 100, maxSteps: 5));
        act.Should().Throw<PatternBudgetExceededException>().Which.BudgetName.Should().Be("MaxSteps");
    }

    [Fact]
    public void One_label_over_one_true_row_costs_three_steps()
    {
        // Program "A" is: 0 label A, 1 done. Step 1: AdvanceAndSchedule pops instruction 0 at input index 0 and
        // parks the thread on the MatchLabel. Step 2: A's define is evaluated at row 0 (true). Step 3:
        // AdvanceAndSchedule pops instruction 1 (Done) after the label is consumed. The input then ends; the final
        // scan for a thread at Done is not a step.
        var (matcher, _) = Build("A");
        var budget = Budget();

        matcher.Run(new TableEvaluator(AllTrue(1, 1)), budget).Matched.Should().BeTrue();

        budget.StepsUsed.Should().Be(3);
    }

    [Theory]
    [InlineData("A{0,2499} B")]   // 5,000 instructions: the reachable-label precomputation recurses 5,000 deep
    [InlineData("A{4999}")]
    [InlineData("(A?){2499} B")]  // 5,000 instructions; its Splits chain through their second branches: 2,500-deep scheduling
    public void An_at_cap_program_runs_without_a_stack_overflow_on_small_stacks(string pattern)
    {
        void RunOnce()
        {
            var (matcher, _) = Build(pattern);   // builds ThreadEquivalence: reachable labels from every instruction
            matcher.Run(new TableEvaluator([[false, true]]), Budget());
        }

        // A recursive traversal of this depth needs far more than 256 KB of stack; the explicit stacks use the heap.
        Exception? failure = null;
        var thread = new Thread(() => { try { RunOnce(); } catch (Exception e) { failure = e; } }, maxStackSize: 256 * 1024);
        thread.Start();
        thread.Join();
        failure.Should().BeNull();

        var onPool = () => Task.Run(RunOnce).Wait();   // the thread-pool thread spec §9.1 names
        onPool.Should().NotThrow();
    }

    // ThreadEquivalence over real defines. Pattern (A | B)+ C compiles to
    // split 1 3 | label A | jump 4 | label B | split 0 5 | label C | done — C's MatchLabel is instruction 5.
    private static ThreadEquivalence EquivalenceFor(string defineOfC)
    {
        var parser = new ExpressionParser(new Dictionary<string, int> { ["A"] = 0, ["B"] = 1, ["C"] = 2 },
            new Dictionary<string, int[]>(), new SimilarityTermTable());
        var program = ProgramCompilerTests.CompileText("(A | B)+ C");
        return new ThreadEquivalence(program, [null, null, parser.ParseDefine(defineOfC)]);
    }

    private static ArrayView View(params int[] labels) => new(labels, labels.Length);

    [Fact]
    public void Threads_are_equivalent_when_the_reachable_defines_read_the_same_rows()
    {
        var eq = EquivalenceFor("FIRST(A.x) > 0");

        eq.Equivalent(1, View(0, 1), 2, View(0, 0), pointer: 5).Should().BeTrue();   // FIRST(A) is row 0 in both
        eq.Equivalent(1, View(0, 0), 2, View(1, 0), pointer: 5).Should().BeFalse();  // row 0 vs row 1
    }

    [Fact]
    public void Threads_are_compared_on_the_labels_a_classifier_navigation_reads()
    {
        var eq = EquivalenceFor("PREV(CLASSIFIER()) = 'A'");

        eq.Equivalent(1, View(0, 0), 2, View(0, 0), pointer: 5).Should().BeTrue();
        eq.Equivalent(1, View(0, 0), 2, View(1, 0), pointer: 5).Should().BeFalse();
    }

    [Fact]
    public void Threads_are_compared_on_the_positions_an_aggregate_reads()
    {
        var eq = EquivalenceFor("COUNT(A.*) = 1");

        eq.Equivalent(1, View(1, 0), 2, View(1, 0), pointer: 5).Should().BeTrue();
        eq.Equivalent(1, View(0, 1), 2, View(1, 0), pointer: 5).Should().BeFalse();
    }

    [Theory]
    [InlineData(1, true)]              // LAST(A, 0) = 3 and LAST(A, 1) = 2 in both threads
    [InlineData(2, false)]             // LAST(A, 2) is row 0 in one thread and row 1 in the other
    [InlineData(2_000_000_000, false)] // offsets past the 4 matched labels resolve -1 in both: same verdict as 2
    public void Threads_are_compared_on_every_last_offset_up_to_the_match_length(int offset, bool equivalent)
    {
        var eq = EquivalenceFor($"LAST(A.x, {offset}) > 0");

        eq.Equivalent(1, View(0, 1, 0, 0), 2, View(1, 0, 0, 0), pointer: 5).Should().Be(equivalent);
        // a difference that no LAST(A, k) reads keeps the threads equivalent at any offset
        eq.Equivalent(1, View(0, 1, 0), 2, View(0, 2, 0), pointer: 5).Should().BeTrue();
    }

    [Theory]
    [InlineData(2, true)]              // PREV(LAST(A), 2) and the trailing label at LAST(A) - 1 agree
    [InlineData(3, false)]             // PREV(LAST(A), 3) is row 0: label B in one thread, C in the other
    [InlineData(2_000_000_000, false)] // the trailing labels reach row 0 too; those before it resolve -1 in both
    public void Threads_are_compared_on_every_trailing_label_up_to_the_match_length(int offset, bool equivalent)
    {
        var eq = EquivalenceFor($"PREV(CLASSIFIER(A), {offset}) = 'A'");

        eq.Equivalent(1, View(1, 0, 0, 0), 2, View(2, 0, 0, 0), pointer: 5).Should().Be(equivalent);
    }

    /// <summary>C's define reads exactly one navigation, so the equivalence at C's instruction (5) is decided by
    /// that navigation's comparison set alone. The reference materializes Trino's allPositionsToCompare.</summary>
    [Theory]
    [InlineData("LAST(A.x, {0}) IS NULL")]
    [InlineData("PREV(LAST(B.x, {0}), 2) IS NULL")]
    [InlineData("FIRST(A.x, {0}) IS NULL")]
    [InlineData("PREV(CLASSIFIER(A), {0}) = 'A'")]
    [InlineData("NEXT(LAST(CLASSIFIER(B), {0}), 1) = 'A'")]
    [InlineData("PREV(LAST(CLASSIFIER(), {0}), 2) = 'A'")]
    [InlineData("PREV(FIRST(CLASSIFIER(A), {0}), 1) = 'A'")]
    public void The_bounded_comparison_equals_trinos_materialized_expansion(string defineFormat)
    {
        var labelArrays = Enumerable.Range(1, 4).SelectMany(AllLabelArrays).ToList();
        foreach (int offset in new[] { 0, 1, 2, 3, 10 })
        {
            var define = string.Format(defineFormat, offset);
            var eq = EquivalenceFor(define);
            var huge = EquivalenceFor(string.Format(defineFormat, 2_000_000_000));
            var (navigation, classifier) = SingleNavigation(define);

            foreach (var first in labelArrays)
            {
                foreach (var second in labelArrays.Where(l => l.Length == first.Length))
                {
                    bool expected = ReferenceAgree(navigation, classifier, View(first), View(second));
                    eq.Equivalent(1, View(first), 2, View(second), pointer: 5).Should().Be(expected,
                        $"{define} over [{string.Join(",", first)}] vs [{string.Join(",", second)}]");

                    // 10 exceeds every length here, so its verdict is also the verdict at any larger offset
                    if (offset == 10)
                    {
                        huge.Equivalent(1, View(first), 2, View(second), pointer: 5).Should().Be(expected,
                            $"offset 2e9: [{string.Join(",", first)}] vs [{string.Join(",", second)}]");
                    }
                }
            }
        }
    }

    private static IEnumerable<int[]> AllLabelArrays(int length) =>
        length == 0
            ? [[]]
            : AllLabelArrays(length - 1).SelectMany(prefix => new[] { 0, 1, 2 }.Select(l => prefix.Append(l).ToArray()));

    private static (Navigation Navigation, bool Classifier) SingleNavigation(string define)
    {
        var parser = new ExpressionParser(new Dictionary<string, int> { ["A"] = 0, ["B"] = 1, ["C"] = 2 },
            new Dictionary<string, int[]>(), new SimilarityTermTable());
        var read = parser.ParseDefine(define) switch
        {
            IsNullExpr isNull => isNull.Operand,
            BinaryExpr binary => binary.Left,
            var other => throw new InvalidOperationException(other.ToString()),
        };
        return read switch
        {
            ColumnExpr { Navigation: { } navigation } => (navigation, false),
            ClassifierExpr { Navigation: { } navigation } => (navigation, true),
            _ => throw new InvalidOperationException(read.ToString()),
        };
    }

    /// <summary>The pre-bound ThreadEquivalence: Trino's allPositionsToCompare, materialized, then resolved.</summary>
    private static bool ReferenceAgree(Navigation navigation, bool classifier, ArrayView first, ArrayView second)
    {
        if (!classifier)
        {
            navigation = navigation.WithPhysicalOffset(0);
        }

        var all = new List<Navigation>();
        if (navigation.Last)
        {
            for (int offset = 0; offset <= navigation.LogicalOffset; offset++) all.Add(navigation.WithLogicalOffset(offset));
            for (int tail = navigation.PhysicalOffset + 1; tail < 0; tail++)
                all.Add(navigation.WithLogicalOffset(0).WithPhysicalOffset(tail));
        }
        else
        {
            all.Add(navigation);
        }

        int Resolve(Navigation n, ArrayView labels) => n.ResolvePosition(labels.Length - 1, labels, 0, labels.Length, 0);
        foreach (var n in all)
        {
            int a = Resolve(n, first), b = Resolve(n, second);
            if (!classifier ? a != b : (a == -1) != (b == -1) || (a != -1 && first[a] != second[b]))
            {
                return false;
            }
        }

        return true;
    }

    [Fact]
    public void A_thread_is_equivalent_to_itself_and_empty_matches_are_equivalent()
    {
        var eq = EquivalenceFor("FIRST(A.x) > 0");

        eq.Equivalent(7, View(0, 0), 7, View(1, 0), pointer: 5).Should().BeTrue();   // same thread: an empty cycle
        eq.Equivalent(1, View(), 2, View(), pointer: 0).Should().BeTrue();
    }
}
