namespace Iverson.Patterns.Expressions;

/// <summary>
/// The state an expression reads. In a <c>define</c>, <see cref="MatchedLabels"/> ends with the label being
/// tried and <see cref="CurrentRow"/> = <see cref="PatternStart"/> + <c>MatchedLabels.Length</c> − 1. For a
/// measure, <see cref="MatchedLabels"/> is the whole match and <see cref="CurrentRow"/> the row the measure is
/// computed for (the last row for <c>ONE_ROW</c>). <see cref="EmptyMatch"/> selects empty-match semantics.
/// </summary>
internal sealed class EvaluationContext
{
    public required IReadOnlyList<IDictionary<string, object?>> Rows { get; init; }
    public required IReadOnlyList<string> LabelNames { get; init; }
    /// <summary>(partition row index, similarity term index) → score, or null for an absent vector.</summary>
    public required Func<int, int, double?> Similarity { get; init; }
    public int PatternStart { get; set; }
    public int CurrentRow { get; set; }
    public ArrayView MatchedLabels { get; set; } = ArrayView.Empty;
    public long MatchNumber { get; set; }
    public bool EmptyMatch { get; set; }
}
