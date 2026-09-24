// Port of Trino 483 io.trino.operator.window.matcher.ThreadEquivalence (Apache-2.0); see THIRD-PARTY-NOTICES.md.
using Iverson.Patterns.Compilation;
using Iverson.Patterns.Expressions;

namespace Iverson.Patterns.Matching;

/// <summary>
/// Determines whether two pattern matching threads are equivalent, so that a thread which duplicates some other
/// thread can be pruned.
/// <para>
/// It is assumed that the two compared threads have already matched the same portion of input (so their arrays of
/// matched labels are of equal lengths) and have reached the same instruction in the program.
/// </para>
/// <para>
/// 1. Get the set of labels reachable by the program from the current instruction until the end of the program
/// (<c>reachableLabels</c>).
/// 2. For all those labels, get all navigating operations for accessing input values which need to be performed
/// during label evaluation (<c>positionsToCompare</c>), and check that they navigate to the same input row for both
/// threads. The physical offsets are stripped, and navigations over the universal pattern variable are skipped.
/// 3. For all those labels, get all navigating operations for <c>CLASSIFIER</c> calls (<c>labelsToCompare</c>), and
/// check that they navigate to rows tagged with the same label (not necessarily the same position) for both threads.
/// <c>MATCH_NUMBER</c> is skipped: it is constant in this context.
/// 4. For all those labels, get all aggregations computed during label evaluation. 4a: for aggregations whose
/// arguments do not depend on the matched labels, check that the aggregated positions are the same for both
/// threads. 4b: for aggregations whose arguments do depend on them, also check the labels at those positions.
/// </para>
/// <para>
/// Deviations from Trino: the reachable-label sets are computed with an explicit stack (no recursion that scales
/// with the program size), and the three comparison sets are derived from each label's lowered <c>define</c>
/// (<paramref name="labelDefinitions"/>, indexed by label, null = no define) instead of from physical value
/// accessors and per-thread <c>MatchAggregation</c>s. An aggregation's positions are recomputed from the matched
/// labels: the relative indexes of the labels in its label set (all of them, for the universal set), which is
/// Trino's <c>SetEvaluator.getAllPositions</c>.
/// </para>
/// </summary>
internal sealed class ThreadEquivalence
{
    // for every pointer (instruction) in the program, the set of labels reachable by the program
    // starting from this instruction until the program ends, through any path.
    private readonly HashSet<int>[] _reachableLabels;

    // for every label, the set of navigations for accessing input values based on the defining condition
    private readonly HashSet<Navigation>[] _positionsToCompare;

    // for every label, the set of navigations for accessing the matched labels based on the defining condition
    private readonly HashSet<Navigation>[] _labelsToCompare;

    // for every label, all aggregations present in the defining condition which require only comparing the
    // aggregated positions; null when no define has one
    private readonly List<AggregateExpr>[]? _matchAggregationsToComparePositions;

    // for every label, all aggregations present in the defining condition which require comparing the aggregated
    // positions and assigned labels; null when no define has one
    private readonly List<AggregateExpr>[]? _matchAggregationsToComparePositionsAndLabels;

    public ThreadEquivalence(Instruction[] program, IReadOnlyList<Expr?> labelDefinitions)
    {
        _reachableLabels = ComputeReachableLabels(program);

        int labelCount = labelDefinitions.Count;
        _positionsToCompare = new HashSet<Navigation>[labelCount];
        _labelsToCompare = new HashSet<Navigation>[labelCount];
        var noClassifierAggregations = new List<AggregateExpr>[labelCount];
        var classifierAggregations = new List<AggregateExpr>[labelCount];
        bool foundNoClassifierAggregations = false;
        bool foundClassifierAggregations = false;

        for (int label = 0; label < labelCount; label++)
        {
            var positions = new HashSet<Navigation>();
            var labels = new HashSet<Navigation>();
            var noClassifier = new List<AggregateExpr>();
            var classifier = new List<AggregateExpr>();

            foreach (var read in Reads(labelDefinitions[label]))
            {
                switch (read)
                {
                    // input value pointers: every read of the input other than CLASSIFIER and MATCH_NUMBER
                    case ColumnExpr { Navigation: { } navigation }:
                        AddInputNavigation(positions, navigation);
                        break;
                    case SimilarityExpr { Navigation: { } navigation }:
                        AddInputNavigation(positions, navigation);
                        break;

                    // classifier value pointers
                    case ClassifierExpr { Navigation: { } navigation }:
                        labels.UnionWith(AllPositionsToCompare(navigation));
                        break;

                    // classifyAggregations: an aggregation does not depend on CLASSIFIER when its arguments do not
                    // call it, or when it applies to only one label (so CLASSIFIER is always the same).
                    case AggregateExpr aggregation:
                        if (!aggregation.ClassifierInvolved || aggregation.Labels.Length == 1)
                        {
                            foundNoClassifierAggregations = true;
                            noClassifier.Add(aggregation);
                        }
                        else
                        {
                            foundClassifierAggregations = true;
                            classifier.Add(aggregation);
                        }

                        break;
                }
            }

            _positionsToCompare[label] = positions;
            _labelsToCompare[label] = labels;
            noClassifierAggregations[label] = noClassifier;
            classifierAggregations[label] = classifier;
        }

        _matchAggregationsToComparePositions = foundNoClassifierAggregations ? noClassifierAggregations : null;
        _matchAggregationsToComparePositionsAndLabels = foundClassifierAggregations ? classifierAggregations : null;
    }

    public bool Equivalent(int firstThread, ArrayView firstLabels, int secondThread, ArrayView secondLabels, int pointer)
    {
        if (firstLabels.Length != secondLabels.Length)
        {
            throw new ArgumentException("matched labels for compared threads differ in length");
        }

        if (pointer < 0 || pointer >= _reachableLabels.Length)
        {
            throw new ArgumentException("instruction pointer out of program bounds");
        }

        if (firstThread == secondThread || firstLabels.Length == 0)
        {
            return true;
        }

        // compare resulting positions for input navigations
        var distinctPositionsToCompare = new HashSet<Navigation>();
        foreach (int label in _reachableLabels[pointer])
        {
            distinctPositionsToCompare.UnionWith(_positionsToCompare[label]);
        }

        foreach (var navigation in distinctPositionsToCompare)
        {
            if (ResolvePosition(navigation, firstLabels) != ResolvePosition(navigation, secondLabels))
            {
                return false;
            }
        }

        // compare resulting labels for `CLASSIFIER` navigations
        var distinctLabelPositionsToCompare = new HashSet<Navigation>();
        foreach (int label in _reachableLabels[pointer])
        {
            distinctLabelPositionsToCompare.UnionWith(_labelsToCompare[label]);
        }

        foreach (var navigation in distinctLabelPositionsToCompare)
        {
            int firstPosition = ResolvePosition(navigation, firstLabels);
            int secondPosition = ResolvePosition(navigation, secondLabels);
            if ((firstPosition == -1) != (secondPosition == -1))
            {
                return false;
            }

            if (firstPosition != -1 && firstLabels[firstPosition] != secondLabels[secondPosition])
            {
                return false;
            }
        }

        // compare sets of all aggregated positions for aggregations which do not depend on `CLASSIFIER`
        if (_matchAggregationsToComparePositions is { } comparePositions)
        {
            var aggregationsToComparePositions = new HashSet<AggregateExpr>(ReferenceEqualityComparer.Instance);
            foreach (int label in _reachableLabels[pointer])
            {
                aggregationsToComparePositions.UnionWith(comparePositions[label]);
            }

            foreach (var aggregation in aggregationsToComparePositions)
            {
                var firstPositions = GetAllPositions(aggregation, firstLabels);
                var secondPositions = GetAllPositions(aggregation, secondLabels);
                if (firstPositions.Length != secondPositions.Length)
                {
                    return false;
                }

                for (int i = 0; i < firstPositions.Length; i++)
                {
                    if (firstPositions[i] != secondPositions[i])
                    {
                        return false;
                    }
                }
            }
        }

        // compare sets of all aggregated positions, and sets of matched labels on all aggregated positions for
        // aggregations which depend on `CLASSIFIER`
        if (_matchAggregationsToComparePositionsAndLabels is { } comparePositionsAndLabels)
        {
            var aggregationsToComparePositionsAndLabels = new HashSet<AggregateExpr>(ReferenceEqualityComparer.Instance);
            foreach (int label in _reachableLabels[pointer])
            {
                aggregationsToComparePositionsAndLabels.UnionWith(comparePositionsAndLabels[label]);
            }

            foreach (var aggregation in aggregationsToComparePositionsAndLabels)
            {
                var firstPositions = GetAllPositions(aggregation, firstLabels);
                var secondPositions = GetAllPositions(aggregation, secondLabels);
                if (firstPositions.Length != secondPositions.Length)
                {
                    return false;
                }

                for (int i = 0; i < firstPositions.Length; i++)
                {
                    int position = firstPositions[i];
                    if (position != secondPositions[i] || firstLabels[position] != secondLabels[position])
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private static int ResolvePosition(Navigation navigation, ArrayView labels) =>
        navigation.ResolvePosition(labels.Length - 1, labels, 0, labels.Length, 0);

    /// <summary>Trino's <c>SetEvaluator.getAllPositions</c>: every position (from 0) of <paramref name="labels"/>
    /// whose label the aggregation applies to. The whole array is scanned whatever the RUNNING/FINAL semantics.</summary>
    private static ArrayView GetAllPositions(AggregateExpr aggregation, ArrayView labels)
    {
        var positions = new IntList(labels.Length);
        for (int position = 0; position < labels.Length; position++)
        {
            if (aggregation.Labels.Length == 0 || Array.BinarySearch(aggregation.Labels, labels[position]) >= 0)
            {
                positions.Add(position);
            }
        }

        return positions.ToArrayView();
    }

    private static HashSet<int>[] ComputeReachableLabels(Instruction[] program)
    {
        var result = new HashSet<int>[program.Length];
        var stack = new Stack<int>();
        for (int start = 0; start < program.Length; start++)
        {
            var labels = new HashSet<int>();
            var visited = new bool[program.Length];
            stack.Push(start);
            while (stack.Count > 0)
            {
                int index = stack.Pop();
                if (visited[index]) continue;
                visited[index] = true;
                var instruction = program[index];
                switch (instruction.Kind)
                {
                    case InstructionKind.MatchLabel:
                        labels.Add(instruction.First);
                        stack.Push(index + 1);
                        break;
                    case InstructionKind.Jump:
                        stack.Push(instruction.First);
                        break;
                    case InstructionKind.Split:
                        stack.Push(instruction.Second);
                        stack.Push(instruction.First);
                        break;
                    case InstructionKind.MatchStart:
                    case InstructionKind.MatchEnd:
                    case InstructionKind.ExclusionStart:
                    case InstructionKind.ExclusionEnd:
                        stack.Push(index + 1);
                        break;
                    case InstructionKind.Done:
                        break;
                }
            }
            result[start] = labels;
        }
        return result;
    }

    /// <summary>Trino's <c>positionsToCompare</c> mapping: skip the universal pattern variable, strip the physical
    /// offset, expand with <see cref="AllPositionsToCompare"/>.</summary>
    private static void AddInputNavigation(HashSet<Navigation> positions, Navigation navigation)
    {
        if (navigation.Labels.Length != 0)
        {
            positions.UnionWith(AllPositionsToCompare(navigation.WithPhysicalOffset(0)));
        }
    }

    /// <summary>
    /// For a navigation, returns all navigations which must return equal results for the two compared threads if
    /// the threads are equivalent.
    /// <code>
    /// FIRST(A.value)    -> compare the position "FIRST(A)"
    /// FIRST(A.value, 2) -> compare the position "FIRST(A, 2)"
    /// LAST(A.value)     -> compare the position "LAST(A)"
    /// LAST(A.value, 2)  -> compare the positions "LAST(A, 2)", "LAST(A, 1)", "LAST(A)".
    /// </code>
    /// They must all be equal for both threads in case there are more labels "A" assigned in the future.
    /// <c>PREV(LAST(CLASSIFIER(A), 2), 5)</c> -> compare the positions "PREV(LAST(A, 2), 5)", "PREV(LAST(A, 1), 5)",
    /// "PREV(LAST(A), 5)", and the 5 trailing labels.
    /// </summary>
    private static List<Navigation> AllPositionsToCompare(Navigation navigation)
    {
        if (navigation.Last)
        {
            var result = new List<Navigation>();
            for (int offset = 0; offset <= navigation.LogicalOffset; offset++)
            {
                result.Add(navigation.WithLogicalOffset(offset));
            }

            // physical offset can be present only in `CLASSIFIER` navigations. For input navigations it was pruned.
            // In case when the physical offset is negative, we need to compare all labels in the offset-length suffix
            // of the match between both compared threads.
            for (int tail = navigation.PhysicalOffset + 1; tail < 0; tail++)
            {
                result.Add(navigation.WithLogicalOffset(0).WithPhysicalOffset(tail));
            }

            return result;
        }

        return [navigation];
    }

    /// <summary>Every column, <c>SIMILARITY</c>, <c>CLASSIFIER</c> and aggregate node of a lowered expression, found
    /// with an explicit stack. Aggregate arguments are walked too: their reads carry no navigation (the parser
    /// forbids navigations inside aggregates) and are ignored by the caller.</summary>
    private static IEnumerable<Expr> Reads(Expr? root)
    {
        if (root is null)
        {
            yield break;
        }

        var stack = new Stack<Expr>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var expr = stack.Pop();
            switch (expr)
            {
                case ColumnExpr or SimilarityExpr or ClassifierExpr:
                    yield return expr;
                    break;
                case AggregateExpr aggregate:
                    yield return aggregate;
                    if (aggregate.Argument is not null) stack.Push(aggregate.Argument);
                    break;
                case UnaryExpr unary:
                    stack.Push(unary.Operand);
                    break;
                case BinaryExpr binary:
                    stack.Push(binary.Left);
                    stack.Push(binary.Right);
                    break;
                case IsNullExpr isNull:
                    stack.Push(isNull.Operand);
                    break;
                case InExpr @in:
                    stack.Push(@in.Operand);
                    foreach (var value in @in.Values) stack.Push(value);
                    break;
                case BetweenExpr between:
                    stack.Push(between.Operand);
                    stack.Push(between.Low);
                    stack.Push(between.High);
                    break;
                case CaseExpr @case:
                    if (@case.Operand is not null) stack.Push(@case.Operand);
                    foreach (var branch in @case.Branches)
                    {
                        stack.Push(branch.When);
                        stack.Push(branch.Then);
                    }

                    if (@case.Else is not null) stack.Push(@case.Else);
                    break;
                case FunctionExpr function:
                    foreach (var argument in function.Arguments) stack.Push(argument);
                    break;
                case TimestampDiffExpr diff:
                    stack.Push(diff.From);
                    stack.Push(diff.To);
                    break;
                case LiteralExpr or MatchNumberExpr:
                    break;
                default:
                    throw new NotSupportedException($"unsupported expression type: {expr.GetType().Name}");
            }
        }
    }
}
