namespace Iverson.Patterns.Expressions;

/// <summary>
/// Evaluates a parsed <c>define</c> or <c>measures</c> expression against an <see cref="EvaluationContext"/>
/// under SQL three-valued logic and the spec §2 NaN rules. Value reads follow Trino 483
/// <c>io.trino.operator.window.pattern.MeasureComputation</c> (<c>compute</c>, <c>computeEmpty</c>; Apache-2.0,
/// see THIRD-PARTY-NOTICES.md): navigations resolve over the whole partition, a read landing nowhere is NULL,
/// and an empty match reads NULL everywhere except <c>MATCH_NUMBER()</c> and <c>COUNT</c> (0).
/// </summary>
internal static class ExpressionEvaluator
{
    /// <summary>No aggregate is being computed: a read must carry its own navigation.</summary>
    private const int NoAggregationRow = -1;

    public static object? Evaluate(Expr expr, EvaluationContext context) => Evaluate(expr, context, NoAggregationRow);

    /// <summary>TRUE labels the row; FALSE and NULL do not.</summary>
    public static bool EvaluateDefine(Expr expr, EvaluationContext context) => Evaluate(expr, context) switch
    {
        null => false,
        bool b => b,
        var other => throw new PatternEvaluationException(
            $"A define expression must evaluate to a boolean, not {SqlValues.TypeName(other)}."),
    };

    private static object? Evaluate(Expr expr, EvaluationContext ctx, int aggregationRow) => expr switch
    {
        LiteralExpr literal => literal.Value,
        ColumnExpr column => ReadColumn(column, ctx, aggregationRow),
        ClassifierExpr classifier => ReadClassifier(classifier, ctx, aggregationRow),
        SimilarityExpr similarity => ReadSimilarity(similarity, ctx, aggregationRow),
        MatchNumberExpr => (object)ctx.MatchNumber,
        AggregateExpr aggregate => Aggregate(aggregate, ctx),
        UnaryExpr unary => EvaluateUnary(unary, ctx, aggregationRow),
        BinaryExpr binary => EvaluateBinary(binary, ctx, aggregationRow),
        IsNullExpr isNull => (object)((Evaluate(isNull.Operand, ctx, aggregationRow) is null) != isNull.Negated),
        InExpr @in => EvaluateIn(@in, ctx, aggregationRow),
        BetweenExpr between => EvaluateBetween(between, ctx, aggregationRow),
        CaseExpr @case => EvaluateCase(@case, ctx, aggregationRow),
        FunctionExpr function => EvaluateFunction(function, ctx, aggregationRow),
        TimestampDiffExpr diff => EvaluateTimestampDiff(diff, ctx, aggregationRow),
        _ => throw new InvalidOperationException($"Unknown expression {expr.GetType().Name}."),
    };

    // --- Reads ---

    /// <returns>The partition row the read lands on, or -1 when it lands nowhere (NULL).</returns>
    private static int Position(Navigation? navigation, EvaluationContext ctx, int aggregationRow)
    {
        if (navigation is null)
        {
            return aggregationRow != NoAggregationRow
                ? aggregationRow
                : throw new InvalidOperationException("A read outside an aggregate must carry a navigation.");
        }

        // MATCH_RECOGNIZE searches the whole partition.
        return navigation.ResolvePosition(ctx.CurrentRow, ctx.MatchedLabels, 0, ctx.Rows.Count, ctx.PatternStart);
    }

    private static object? ReadColumn(ColumnExpr column, EvaluationContext ctx, int aggregationRow)
    {
        if (ctx.EmptyMatch)
        {
            return null;
        }

        int position = Position(column.Navigation, ctx, aggregationRow);
        if (position == -1)
        {
            return null;
        }

        return ctx.Rows[position].TryGetValue(column.Column, out var value)
            ? SqlValues.Normalize(value, column.Column)
            : throw new PatternEvaluationException($"Column '{column.Column}' is not in the row.");
    }

    private static object? ReadClassifier(ClassifierExpr classifier, EvaluationContext ctx, int aggregationRow)
    {
        if (ctx.EmptyMatch)
        {
            return null;
        }

        int position = Position(classifier.Navigation, ctx, aggregationRow);
        int relative = position - ctx.PatternStart;

        // Outside the match → NULL; inside it, the label even past the running position (MeasureComputation).
        return position == -1 || relative < 0 || relative >= ctx.MatchedLabels.Length
            ? null
            : ctx.LabelNames[ctx.MatchedLabels[relative]];
    }

    private static object? ReadSimilarity(SimilarityExpr similarity, EvaluationContext ctx, int aggregationRow)
    {
        if (ctx.EmptyMatch)
        {
            return null;
        }

        int position = Position(similarity.Navigation, ctx, aggregationRow);
        return position == -1 ? null : ctx.Similarity(position, similarity.Term);
    }

    // --- Aggregates ---

    private static object? Aggregate(AggregateExpr aggregate, EvaluationContext ctx)
    {
        if (ctx.EmptyMatch)
        {
            return aggregate.Kind == AggregateKind.Count ? 0L : null;
        }

        int end = aggregate.Running ? ctx.CurrentRow - ctx.PatternStart : ctx.MatchedLabels.Length - 1;
        end = Math.Min(end, ctx.MatchedLabels.Length - 1);

        long positions = 0;
        var values = new List<object>();
        for (int relative = 0; relative <= end; relative++)
        {
            if (aggregate.Labels.Length != 0 && Array.BinarySearch(aggregate.Labels, ctx.MatchedLabels[relative]) < 0)
            {
                continue;
            }

            positions++;
            if (aggregate.Argument is not null &&
                Evaluate(aggregate.Argument, ctx, ctx.PatternStart + relative) is { } value)
            {
                values.Add(value);
            }
        }

        return aggregate.Kind switch
        {
            // COUNT(*) / COUNT(v.*) count rows; COUNT(e) counts non-null values (NaN is not null).
            AggregateKind.Count => (object)(aggregate.Argument is null ? positions : values.Count),
            AggregateKind.Sum => Sum(values),
            AggregateKind.Avg => values.Count == 0 ? null : (object)(ToDouble(Sum(values)!) / values.Count),
            AggregateKind.Min => Extreme(values, max: false),
            AggregateKind.Max => Extreme(values, max: true),
            _ => throw new InvalidOperationException($"Unknown aggregate {aggregate.Kind}."),
        };
    }

    /// <returns>A checked <c>long</c> when every value is a <c>long</c>, else an IEEE <c>double</c> (NaN
    /// propagates); <c>null</c> for no values.</returns>
    private static object? Sum(List<object> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        foreach (var value in values)
        {
            if (!SqlValues.IsNumeric(value))
            {
                throw new PatternEvaluationException($"Cannot sum {SqlValues.TypeName(value)}.");
            }
        }

        if (values.TrueForAll(value => value is long))
        {
            long total = 0;
            try
            {
                foreach (var value in values)
                {
                    total = checked(total + (long)value);
                }
            }
            catch (OverflowException)
            {
                throw new PatternEvaluationException("Integer overflow.");
            }

            return total;
        }

        double sum = 0;
        foreach (var value in values)
        {
            sum += SqlValues.ToDouble(value);
        }

        return sum;
    }

    private static double ToDouble(object value) => SqlValues.ToDouble(value);

    /// <summary>MIN/MAX over non-null values. Numeric pairs involving a <c>double</c> order by
    /// <see cref="double.CompareTo(double)"/>, where NaN is lowest: MIN returns NaN if present, MAX ignores NaN
    /// unless every value is NaN.</summary>
    private static object? Extreme(List<object> values, bool max)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var best = values[0];
        for (int i = 1; i < values.Count; i++)
        {
            var candidate = values[i];
            bool better;
            if ((candidate is double || best is double) && SqlValues.IsNumeric(candidate) && SqlValues.IsNumeric(best))
            {
                int order = SqlValues.ToDouble(candidate).CompareTo(SqlValues.ToDouble(best));
                better = max ? order > 0 : order < 0;
            }
            else
            {
                better = SqlValues.Compare(candidate, best, max ? BinaryOp.Greater : BinaryOp.Less);
            }

            if (better)
            {
                best = candidate;
            }
        }

        return best;
    }

    // --- Operators ---

    private static object? EvaluateUnary(UnaryExpr unary, EvaluationContext ctx, int aggregationRow)
    {
        var operand = Evaluate(unary.Operand, ctx, aggregationRow);
        return unary.Op switch
        {
            UnaryOp.Not => Not(AsBoolean(operand, "NOT")),
            UnaryOp.Negate => operand is null ? null : SqlValues.Negate(operand),
            _ => throw new InvalidOperationException($"Unknown unary operator {unary.Op}."),
        };
    }

    private static object? EvaluateBinary(BinaryExpr binary, EvaluationContext ctx, int aggregationRow)
    {
        switch (binary.Op)
        {
            case BinaryOp.And:
            {
                // Short-circuits on FALSE, as Trino's generated AND does.
                var left = AsBoolean(Evaluate(binary.Left, ctx, aggregationRow), "AND");
                if (left == false)
                {
                    return false;
                }

                var right = AsBoolean(Evaluate(binary.Right, ctx, aggregationRow), "AND");
                return And(left, right);
            }
            case BinaryOp.Or:
            {
                // Short-circuits on TRUE, as Trino's generated OR does.
                var left = AsBoolean(Evaluate(binary.Left, ctx, aggregationRow), "OR");
                if (left == true)
                {
                    return true;
                }

                var right = AsBoolean(Evaluate(binary.Right, ctx, aggregationRow), "OR");
                return Or(left, right);
            }
            case BinaryOp.Add or BinaryOp.Subtract or BinaryOp.Multiply or BinaryOp.Divide or BinaryOp.Modulo:
            {
                var left = Evaluate(binary.Left, ctx, aggregationRow);
                var right = Evaluate(binary.Right, ctx, aggregationRow);
                return left is null || right is null ? null : SqlValues.Arithmetic(binary.Op, left, right);
            }
            default:
                return Compare(Evaluate(binary.Left, ctx, aggregationRow), Evaluate(binary.Right, ctx, aggregationRow), binary.Op);
        }
    }

    /// <summary>A comparison under three-valued logic: a NULL operand gives NULL.</summary>
    private static bool? Compare(object? left, object? right, BinaryOp op) =>
        left is null || right is null ? null : SqlValues.Compare(left, right, op);

    private static bool? EvaluateIn(InExpr @in, EvaluationContext ctx, int aggregationRow)
    {
        var operand = Evaluate(@in.Operand, ctx, aggregationRow);

        // OR over '=': TRUE if any is TRUE, else NULL if any is NULL, else FALSE.
        bool? result = false;
        foreach (var value in @in.Values)
        {
            result = Or(result, Compare(operand, Evaluate(value, ctx, aggregationRow), BinaryOp.Equal));
            if (result == true)
            {
                break;
            }
        }

        return @in.Negated ? Not(result) : result;
    }

    private static bool? EvaluateBetween(BetweenExpr between, EvaluationContext ctx, int aggregationRow)
    {
        var operand = Evaluate(between.Operand, ctx, aggregationRow);
        var low = Evaluate(between.Low, ctx, aggregationRow);
        var high = Evaluate(between.High, ctx, aggregationRow);

        var result = And(Compare(operand, low, BinaryOp.GreaterOrEqual), Compare(operand, high, BinaryOp.LessOrEqual));
        return between.Negated ? Not(result) : result;
    }

    private static object? EvaluateCase(CaseExpr @case, EvaluationContext ctx, int aggregationRow)
    {
        var operand = @case.Operand is null ? null : Evaluate(@case.Operand, ctx, aggregationRow);

        foreach (var branch in @case.Branches)
        {
            var when = Evaluate(branch.When, ctx, aggregationRow);

            // Simple CASE: '=' must be TRUE, so NULL never matches. Searched CASE: the WHEN must be TRUE.
            bool? matched = @case.Operand is null ? AsBoolean(when, "CASE WHEN") : Compare(operand, when, BinaryOp.Equal);
            if (matched == true)
            {
                return Evaluate(branch.Then, ctx, aggregationRow);
            }
        }

        return @case.Else is null ? null : Evaluate(@case.Else, ctx, aggregationRow);
    }

    // --- Functions ---

    private static object? EvaluateFunction(FunctionExpr function, EvaluationContext ctx, int aggregationRow)
    {
        var arguments = function.Arguments;
        switch (function.Function)
        {
            case ScalarFunction.Coalesce:
                foreach (var argument in arguments)
                {
                    if (Evaluate(argument, ctx, aggregationRow) is { } value)
                    {
                        return value;
                    }
                }

                return null;

            case ScalarFunction.NullIf:
            {
                var a = Evaluate(arguments[0], ctx, aggregationRow);
                var b = Evaluate(arguments[1], ctx, aggregationRow);
                return Compare(a, b, BinaryOp.Equal) == true ? null : a;
            }

            case ScalarFunction.Round:
            {
                var value = Evaluate(arguments[0], ctx, aggregationRow);
                if (value is null)
                {
                    return null;
                }

                if (arguments.Count == 1)
                {
                    return value switch
                    {
                        long => value,
                        double d => (object)Math.Round(d, MidpointRounding.AwayFromZero),
                        _ => throw new PatternEvaluationException($"Cannot ROUND {SqlValues.TypeName(value)}."),
                    };
                }

                // The parser admits only a non-negative integer literal here.
                var digits = (long)Evaluate(arguments[1], ctx, aggregationRow)!;
                return value switch
                {
                    long => value,
                    // A double carries at most ~15–17 significant digits, and Math.Round throws past 15.
                    double d => (object)(digits > 15 ? d : Math.Round(d, (int)digits, MidpointRounding.AwayFromZero)),
                    _ => throw new PatternEvaluationException($"Cannot ROUND {SqlValues.TypeName(value)}."),
                };
            }

            case ScalarFunction.Abs:
            {
                var value = Evaluate(arguments[0], ctx, aggregationRow);
                return value switch
                {
                    null => null,
                    long l => l == long.MinValue ? throw new PatternEvaluationException("Integer overflow.") : (object)Math.Abs(l),
                    double d => (object)Math.Abs(d),
                    _ => throw new PatternEvaluationException($"Cannot ABS {SqlValues.TypeName(value)}."),
                };
            }

            default:
                throw new InvalidOperationException($"Unknown function {function.Function}.");
        }
    }

    private static object? EvaluateTimestampDiff(TimestampDiffExpr diff, EvaluationContext ctx, int aggregationRow)
    {
        var from = Evaluate(diff.From, ctx, aggregationRow);
        var to = Evaluate(diff.To, ctx, aggregationRow);
        if (from is null || to is null)
        {
            return null;
        }

        if (from is not DateTime start || to is not DateTime finish)
        {
            throw new PatternEvaluationException(
                $"TIMESTAMPDIFF needs two timestamps, not {SqlValues.TypeName(from)} and {SqlValues.TypeName(to)}.");
        }

        long ticksPerUnit = diff.Unit switch
        {
            TimeUnit.Second => TimeSpan.TicksPerSecond,
            TimeUnit.Minute => TimeSpan.TicksPerMinute,
            TimeUnit.Hour => TimeSpan.TicksPerHour,
            TimeUnit.Day => TimeSpan.TicksPerDay,
            _ => throw new InvalidOperationException($"Unknown unit {diff.Unit}."),
        };

        // Integer division truncates toward zero: whole units only.
        return (finish - start).Ticks / ticksPerUnit;
    }

    // --- Three-valued logic ---

    private static bool? AsBoolean(object? value, string where) => value switch
    {
        null => null,
        bool b => b,
        _ => throw new PatternEvaluationException($"{where} needs a boolean operand, not {SqlValues.TypeName(value)}."),
    };

    private static bool? Not(bool? value) => value is null ? null : !value.Value;

    private static bool? And(bool? left, bool? right) =>
        left == false || right == false ? false : left is null || right is null ? null : true;

    private static bool? Or(bool? left, bool? right) =>
        left == true || right == true ? true : left is null || right is null ? null : false;
}
