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

public interface IEntityRepository
{
    Task<string?> FetchByKeyAsync(TableSchema schema, string key);
    Task<IEnumerable<KeyedRow>> FetchManyByKeysAsync(TableSchema schema, IReadOnlyList<string> keys);
    Task<IEnumerable<string>> FetchByColumnAsync(TableSchema schema, string columnName, string value);
    Task<IEnumerable<string>> FetchAllAsync(TableSchema schema);
    Task DeleteAsync(IDbTransactionContext tx, TableSchema schema, string key);
}

public interface ISchemaRegistryRepository
{
    Task EnsureTableAsync();
    Task<IEnumerable<(string TypeName, string SchemaJson)>> LoadAllAsync();
    Task UpsertAsync(string typeName, string schemaJson);
    Task DeleteAsync(string typeName);
}

public interface IReconciliationQueueRepository
{
    Task<IEnumerable<ReconciliationQueueRow>> PollQueuedFailuresAsync(int maxAttempts, int batchSize);
    Task<int> CountExhaustedAsync(int maxAttempts);
    Task RecordFailureAsync(Guid id, int attempts, string lastError);
    Task DeleteRowAsync(Guid id);
}

public interface IDlqRepository
{
    Task InsertAsync(DlqMessage message);
    Task<IEnumerable<DlqRow>> ListUnreplayedAsync(int limit);
    Task<DlqReplayRow?> GetUnreplayedByIdAsync(Guid id);
    Task MarkReplayedAsync(Guid id);
}

public sealed record TableSchema(
    string TableName,
    ColumnSchema KeyColumn,
    IReadOnlyList<ColumnSchema> Columns);

public sealed record ColumnSchema(string Name, string SqlType, bool IsNullable);
