namespace Iverson.Sql;

public interface IRecordStoreQueryExecutor
{
    Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null);
    Task<int> ExecuteAsync(string sql, object? param = null);
    Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? param = null);
}

public interface IRecordStoreSchemaManager
{
    Task ApplySchemaAsync(TableSchema schema);
}

public interface IRecordStoreTransactionRunner
{
    Task ExecuteInTransactionAsync(Func<IDbTransactionContext, Task> work);
    Task<T> ExecuteInTransactionAsync<T>(Func<IDbTransactionContext, Task<T>> work);
}

public interface IDbTransactionContext
{
    Task<int> ExecuteAsync(string sql, object? param = null);
    Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? param = null);
}

public sealed record TableSchema(
    string TableName,
    ColumnSchema KeyColumn,
    IReadOnlyList<ColumnSchema> Columns);

public sealed record ColumnSchema(string Name, string SqlType, bool IsNullable);
