namespace Iverson.StarRocks;

public enum AggregationKind
{
    Terms,
    DateHistogram,
    Range,
    Avg,
    Sum,
    Min,
    Max,
    Count
}

public sealed record AggregationDescriptor(
    string Name,
    AggregationKind Kind,
    string Field,
    int Size = 10,
    string? CalendarInterval = null,
    string? TimeZone = null,
    IReadOnlyList<RangeBucketDescriptor>? RangeBuckets = null,
    IReadOnlyList<string>? GroupByFields = null,
    string? Expression = null);

public sealed record RangeBucketDescriptor(string Key, double? From, double? To);

public sealed record AggregationResult(
    string Name,
    AggregationKind Kind,
    IReadOnlyList<AggregationBucket>? Buckets = null,
    double? MetricValue = null);

public sealed record AggregationBucket(string Key, long DocCount);

// ── Compound GROUP BY (GroupByRequest) ──────────────────────────────────────

public sealed record GroupByDescriptor(
    IReadOnlyList<string>           Keys,
    IReadOnlyList<MetricDescriptor> Metrics,
    IReadOnlyList<SortDescriptor>?  OrderBy  = null,
    int                             Limit    = 10_000,
    string?                         Having   = null);   // pre-built HAVING SQL fragment

public sealed record MetricDescriptor(
    string          Name,
    AggregationKind Kind,
    string?         Field      = null,
    string?         Expression = null);

public sealed record SortDescriptor(string Column, bool Descending = false);
