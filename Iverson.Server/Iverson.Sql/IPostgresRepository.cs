namespace Iverson.Sql;

public interface IPostgresRepository
{
    Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null);
    Task<int> ExecuteAsync(string sql, object? param = null);
    Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? param = null);
    Task ApplySchemaAsync(TableSchema schema);
}

public sealed record TableSchema(
    string TableName,
    ColumnSchema KeyColumn,
    IReadOnlyList<ColumnSchema> Columns);

public sealed record ColumnSchema(string Name, string SqlType, bool IsNullable);

