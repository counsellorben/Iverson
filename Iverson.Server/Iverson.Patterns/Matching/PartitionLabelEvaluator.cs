using Iverson.Patterns.Expressions;

namespace Iverson.Patterns.Matching;

/// <summary>
/// The counterpart of Trino 483's <c>LabelEvaluator</c>: evaluates <c>define</c> conditions for one match attempt
/// over a partition held in an <see cref="EvaluationContext"/>. A label with no <c>define</c> is always true. The
/// matcher charges the <see cref="PatternBudget"/> step for every evaluation; this class does not.
/// </summary>
/// <param name="defines">The <c>define</c> expression of every label, by label index; null = always true.</param>
internal sealed class PartitionLabelEvaluator(EvaluationContext context, IReadOnlyList<Expr?> defines, int patternStart)
    : ILabelEvaluator
{
    public int InputLength => context.Rows.Count - patternStart;

    public bool MatchingAtPartitionStart => patternStart == 0;

    public bool EvaluateLabel(ArrayView matchedLabels)
    {
        context.PatternStart = patternStart;
        context.EmptyMatch = false;
        context.MatchedLabels = matchedLabels;
        context.CurrentRow = patternStart + matchedLabels.Length - 1;

        var define = defines[matchedLabels[matchedLabels.Length - 1]];
        return define is null || ExpressionEvaluator.EvaluateDefine(define, context);
    }
}
