namespace Iverson.Patterns.Expressions;

/// <summary>
/// Where a value read lands in the partition (port of Trino 483
/// <c>io.trino.operator.window.pattern.LogicalIndexNavigation</c>, Apache-2.0; see THIRD-PARTY-NOTICES.md).
/// <see cref="Labels"/> is sorted and distinct; empty means the universal row pattern variable.
/// Equality is structural, because <c>ThreadEquivalence</c> collects navigations in sets.
/// </summary>
internal sealed record Navigation(int[] Labels, bool Last, bool Running, int LogicalOffset, int PhysicalOffset)
{
    /// <returns>A position in the partition, or -1 when the navigation lands outside the match
    /// (logically) or outside <c>[searchStart, searchEnd)</c> (physically).</returns>
    public int ResolvePosition(int currentRow, ArrayView matchedLabels, int searchStart, int searchEnd, int patternStart)
    {
        int patternEndInclusive = Running ? currentRow - patternStart : matchedLabels.Length - 1;
        int relative = Last ? FindLastAndBackwards(patternEndInclusive, matchedLabels)
                            : FindFirstAndForward(patternEndInclusive, matchedLabels);
        if (relative == -1)
            return -1;
        int target = relative + patternStart + PhysicalOffset;
        return target < searchStart || target >= searchEnd ? -1 : target;
    }

    private int FindLastAndBackwards(int patternEndInclusive, ArrayView matchedLabels)
    {
        int position = patternEndInclusive + 1, found = 0;
        while (found <= LogicalOffset && position > 0)
        {
            position--;
            if (Matches(matchedLabels[position])) found++;
        }
        return found == LogicalOffset + 1 ? position : -1;
    }

    private int FindFirstAndForward(int patternEndInclusive, ArrayView matchedLabels)
    {
        int position = -1, found = 0;
        while (found <= LogicalOffset && position < patternEndInclusive)
        {
            position++;
            if (Matches(matchedLabels[position])) found++;
        }
        return found == LogicalOffset + 1 ? position : -1;
    }

    /// <summary>The nearest position before <paramref name="position"/> whose label this navigation reads, or -1:
    /// one step of the backward scan <c>LAST</c> resolves with. <c>ThreadEquivalence</c> walks a <c>LAST</c>
    /// navigation's logical offsets with it.</summary>
    internal int PreviousMatch(int position, ArrayView matchedLabels)
    {
        while (position > 0)
        {
            position--;
            if (Matches(matchedLabels[position])) return position;
        }
        return -1;
    }

    private bool Matches(int label) => Labels.Length == 0 || Array.BinarySearch(Labels, label) >= 0;

    public Navigation WithLogicalOffset(int offset) => this with { LogicalOffset = offset };
    public Navigation WithPhysicalOffset(int offset) => this with { PhysicalOffset = offset };

    public bool Equals(Navigation? other) =>
        other is not null && Last == other.Last && Running == other.Running &&
        LogicalOffset == other.LogicalOffset && PhysicalOffset == other.PhysicalOffset &&
        Labels.AsSpan().SequenceEqual(other.Labels);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Last); hash.Add(Running); hash.Add(LogicalOffset); hash.Add(PhysicalOffset);
        foreach (var label in Labels) hash.Add(label);
        return hash.ToHashCode();
    }
}
