using Iverson.Client.Contracts;

namespace Iverson.StarRocks;

/// <summary>
/// The TYPE_ROWS parts of a MatchPattern request that decide which rows are read and how (spec §3.2).
/// <see cref="ReferencedColumns"/> are the columns <c>define</c>/<c>measures</c>/<c>SIMILARITY</c> read, as the
/// engine reports them (<c>CompiledPattern.ReferencedColumns</c>); <see cref="ExcludedColumns"/> are the type's
/// bytes columns, removed from the <c>ColumnsFor</c> set before validation and projection.
/// </summary>
public sealed record MatchRowsRequest(
    IReadOnlyList<SearchClause> Where,
    SearchLogic WhereLogic,
    IReadOnlyList<string> PartitionBy,
    IReadOnlyList<SearchSort> OrderBy,
    IReadOnlyCollection<string> ReferencedColumns,
    IReadOnlyList<string> MeasureNames,
    bool OneRowPerMatch,
    IReadOnlyCollection<string> ExcludedColumns,
    int MaxRowsScanned);
