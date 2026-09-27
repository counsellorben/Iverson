// Port of Trino 483 io.trino.operator.window.matcher.MatchResult (Apache-2.0); see THIRD-PARTY-NOTICES.md.
namespace Iverson.Patterns.Matching;

/// <summary>Labels of the matched rows (relative to the pattern start) and exclusion (start, end) pairs.</summary>
internal sealed record MatchResult(bool Matched, ArrayView Labels, ArrayView Exclusions)
{
    public static readonly MatchResult NoMatch = new(false, ArrayView.Empty, ArrayView.Empty);
}
