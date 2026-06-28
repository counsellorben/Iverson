namespace Iverson.StarRocks;

public sealed record StarRocksTableSchema(
    string TableName,
    StarRocksColumnSchema KeyColumn,
    IReadOnlyList<StarRocksColumnSchema> Columns)
{
    public IReadOnlyList<string> MvSortKey         { get; init; } = [];
    public IReadOnlySet<string>  MvExcludedColumns { get; init; } = new HashSet<string>();
}

public sealed record StarRocksColumnSchema(
    string Name,
    string SrType,
    bool IsNullable);
