namespace Iverson.Patterns.Syntax;

internal abstract record PatternNode;

internal sealed record LabelNode(string Name) : PatternNode
{
    public override string ToString() => Name;
}

internal sealed record EmptyNode : PatternNode
{
    public override string ToString() => "()";
}

internal sealed record AnchorNode(bool PartitionStart) : PatternNode
{
    public override string ToString() => PartitionStart ? "^" : "$";
}

internal sealed record ExclusionNode(PatternNode Pattern) : PatternNode
{
    public override string ToString() => $"{{- {Pattern} -}}";
}

internal sealed record AlternationNode(IReadOnlyList<PatternNode> Patterns) : PatternNode
{
    public override string ToString() => "(" + string.Join(" | ", Patterns) + ")";
}

internal sealed record ConcatenationNode(IReadOnlyList<PatternNode> Patterns) : PatternNode
{
    public override string ToString() => "(" + string.Join(" ", Patterns) + ")";
}

internal sealed record PermutationNode(IReadOnlyList<PatternNode> Patterns) : PatternNode
{
    public override string ToString() => "PERMUTE(" + string.Join(", ", Patterns) + ")";
}

/// <summary><see cref="Max"/> null = unbounded. <c>*</c> is (0, null), <c>+</c> (1, null), <c>?</c> (0, 1).</summary>
internal sealed record QuantifiedNode(PatternNode Pattern, int Min, int? Max, bool Greedy) : PatternNode
{
    public override string ToString() =>
        $"{Pattern}{{{Min},{(Max is { } max ? max.ToString(System.Globalization.CultureInfo.InvariantCulture) : "")}}}{(Greedy ? "" : "?")}";
}

/// <param name="Variables">Primary pattern variables, upper case, in order of first appearance.</param>
/// <param name="Subsets">Upper-case subset name → its upper-case member variables.</param>
internal sealed record ParsedPattern(
    PatternNode Root,
    IReadOnlyList<string> Variables,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Subsets,
    bool HasExclusion);
