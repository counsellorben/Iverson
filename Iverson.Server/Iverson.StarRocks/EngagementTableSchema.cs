namespace Iverson.StarRocks;

public sealed record EngagementTableSchema(
    string TableName,
    EngagementColumnSchema KeyColumn,
    IReadOnlyList<EngagementColumnSchema> Columns)
{
    public IReadOnlyList<string> SortKey { get; init; } = [];
}

public sealed record EngagementColumnSchema(
    string Name,
    string SrType,
    bool IsNullable);
