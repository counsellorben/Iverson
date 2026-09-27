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
}
