namespace Iverson.Patterns;

public enum PatternSource { TypeRows, Chunks }

public enum RowsPerMatch { OneRow, AllRowsShowEmpty, AllRowsOmitEmpty, AllRowsWithUnmatched }

public enum AfterMatchSkipKind { PastLastRow, ToNextRow, ToFirst, ToLast }

public sealed record NamedExpression(string Name, string Expression);

public sealed record SubsetDefinition(string Name, IReadOnlyList<string> Variables);

/// <summary>
/// Everything <c>PatternQuery.Compile</c> needs from a MatchPattern request (spec §1–§2). The Api maps the
/// proto onto this in Plan 2; the engine never sees the proto. <see cref="PartitionBy"/> is carried because a
/// <c>ONE_ROW</c> output row starts with the partition columns, and <see cref="TenantColumn"/> because
/// <c>Compile</c> owns the reserved-tenant-name check on <c>measures</c> names.
/// </summary>
public sealed record PatternRequest(
    PatternSource Source,
    IReadOnlyList<string> PartitionBy,
    string Pattern,
    IReadOnlyList<SubsetDefinition> Subsets,
    IReadOnlyList<NamedExpression> Define,
    IReadOnlyList<NamedExpression> Measures,
    RowsPerMatch RowsPerMatch,
    AfterMatchSkipKind AfterMatchSkip,
    string SkipVariable,
    string? TenantColumn);
