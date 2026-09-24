namespace Iverson.Patterns.Matching;

/// <summary>Port of Trino 483 <c>LabelEvaluator</c>'s contract with the matcher.</summary>
internal interface ILabelEvaluator
{
    /// <summary>Rows from the pattern start to the end of the search area.</summary>
    int InputLength { get; }
    bool MatchingAtPartitionStart { get; }
    /// <summary>Evaluates the last label of <paramref name="matchedLabels"/>, tentatively appended.</summary>
    bool EvaluateLabel(ArrayView matchedLabels);
}
