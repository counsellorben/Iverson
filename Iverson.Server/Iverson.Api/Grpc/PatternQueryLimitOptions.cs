namespace Iverson.Api.Grpc;

/// <summary>
/// MatchPattern's limits (spec §5, §4). Built in Program.cs from individual <c>Patterns:Limits:*</c> reads, the same
/// way as <c>EngagementQueryLimitOptions</c>, and injected as a plain singleton.
/// </summary>
public sealed class PatternQueryLimitOptions
{
    public const string Section = "Patterns:Limits";

    /// <summary>Output rows when the request's <c>limit</c> is 0.</summary>
    public const int DefaultOutputRows = 1_000;

    public int MaxPatternLength { get; init; } = 1_000;
    public int MaxProgramInstructions { get; init; } = 5_000;
    public int MaxExpressionLength { get; init; } = 1_000;
    public int MaxDefines { get; init; } = 50;
    public int MaxMeasures { get; init; } = 50;
    public int MaxSubsets { get; init; } = 50;
    public int MaxSimilarityTerms { get; init; } = 10;
    public int MaxOutputRows { get; init; } = 10_000;
    public int MaxRowsScanned { get; init; } = 100_000;
    public int MaxPartitionRows { get; init; } = 10_000;
    public int MaxActiveThreads { get; init; } = 10_000;
    public long MaxSteps { get; init; } = 10_000_000;
    public int TimeoutSeconds { get; init; } = 30;
    public int BatchRows { get; init; } = 2_000;

    public static PatternQueryLimitOptions Default { get; } = new();

    /// <summary>The largest <see cref="TimeoutSeconds"/> <c>CancelAfter</c> accepts (its limit is 4,294,967,294 ms).</summary>
    public const int MaxTimeoutSeconds = 4_294_967;

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> naming every invalid key: every limit must be greater than 0, and
    /// <see cref="MaxRowsScanned"/> less than <see cref="int.MaxValue"/>, since the row read asks for one row more, and
    /// <see cref="TimeoutSeconds"/> at most <see cref="MaxTimeoutSeconds"/>, or every request's <c>CancelAfter</c> throws.
    /// </summary>
    public void Validate()
    {
        var invalid = new List<string>();
        void Positive(string key, long value)
        {
            if (value <= 0) invalid.Add($"{Section}:{key} must be greater than 0 (got {value})");
        }

        Positive(nameof(MaxPatternLength), MaxPatternLength);
        Positive(nameof(MaxProgramInstructions), MaxProgramInstructions);
        Positive(nameof(MaxExpressionLength), MaxExpressionLength);
        Positive(nameof(MaxDefines), MaxDefines);
        Positive(nameof(MaxMeasures), MaxMeasures);
        Positive(nameof(MaxSubsets), MaxSubsets);
        Positive(nameof(MaxSimilarityTerms), MaxSimilarityTerms);
        Positive(nameof(MaxOutputRows), MaxOutputRows);
        Positive(nameof(MaxRowsScanned), MaxRowsScanned);
        if (MaxRowsScanned == int.MaxValue)
            invalid.Add($"{Section}:{nameof(MaxRowsScanned)} must be less than {int.MaxValue}: the row read asks for one row more");
        Positive(nameof(MaxPartitionRows), MaxPartitionRows);
        Positive(nameof(MaxActiveThreads), MaxActiveThreads);
        Positive(nameof(MaxSteps), MaxSteps);
        Positive(nameof(TimeoutSeconds), TimeoutSeconds);
        if (TimeoutSeconds > MaxTimeoutSeconds)
            invalid.Add($"{Section}:{nameof(TimeoutSeconds)} must be at most {MaxTimeoutSeconds} (got {TimeoutSeconds})");
        Positive(nameof(BatchRows), BatchRows);

        if (invalid.Count > 0)
            throw new InvalidOperationException($"Invalid MatchPattern limits: {string.Join("; ", invalid)}.");
    }
}
