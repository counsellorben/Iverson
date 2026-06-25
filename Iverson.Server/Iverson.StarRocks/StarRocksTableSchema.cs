namespace Iverson.StarRocks;

public sealed record StarRocksTableSchema(
    string TableName,
    StarRocksColumnSchema KeyColumn,
    IReadOnlyList<StarRocksColumnSchema> Columns);

public sealed record StarRocksColumnSchema(string Name, string SrType, bool IsNullable);
