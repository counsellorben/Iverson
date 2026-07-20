namespace Iverson.Sql;

public interface IRecordStoreQueryExecutor
{
    Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null, bool tenantScoped = false, string? tenantId = null);
    Task<int> ExecuteAsync(string sql, object? param = null, bool tenantScoped = false, string? tenantId = null);
    Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? param = null, bool tenantScoped = false, string? tenantId = null);
}

public interface IRecordStoreSchemaManager
{
    Task ApplySchemaAsync(TableSchema schema);
    Task EnsureRuntimeRoleAsync();
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

/// <summary>
/// Enter/exit the non-superuser <c>iverson_runtime</c> role + RLS tenant GUC for the remainder of
/// an in-flight transaction. <c>SET LOCAL ROLE</c> persists for the rest of the transaction, not
/// just the next statement, so any subsequent statement in the same transaction against a plumbing
/// table with no <c>iverson_runtime</c> grant (e.g. the reconciliation/outbox queue) must run after
/// <see cref="ExitTenantScopeAsync"/> resets back to the superuser role. Callers that switch into
/// tenant scope mid-transaction must always pair it with a reset — hand-duplicating this sequence
/// is exactly how that pairing gets missed.
/// </summary>
public static class TenantScopeTransactionExtensions
{
    public static async Task EnterTenantScopeAsync(this IDbTransactionContext tx, string? tenantId)
    {
        await tx.ExecuteAsync("SET LOCAL ROLE iverson_runtime");
        await tx.ExecuteAsync("SELECT set_config('app.tenant_id', @TenantId, true)", new { TenantId = tenantId });
    }

    public static Task ExitTenantScopeAsync(this IDbTransactionContext tx) =>
        tx.ExecuteAsync("RESET ROLE");
}

public interface IEntityRepository
{
    Task<string?> FetchByKeyAsync(TableSchema schema, string key, bool tenantScoped = false, string? tenantId = null);
    Task<IEnumerable<KeyedRow>> FetchManyByKeysAsync(TableSchema schema, IReadOnlyList<string> keys, bool tenantScoped = false, string? tenantId = null);
    Task<IEnumerable<string>> FetchByColumnAsync(TableSchema schema, string columnName, string value, bool tenantScoped = false, string? tenantId = null);
    Task<IEnumerable<string>> FetchAllAsync(TableSchema schema, bool tenantScoped = false, string? tenantId = null);
    Task DeleteAsync(IDbTransactionContext tx, TableSchema schema, string key, bool tenantScoped = false, string? tenantId = null);
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
    Task<int> CountPendingAsync();
    Task RecordFailureAsync(Guid id, int attempts, string lastError);
    Task DeleteRowAsync(Guid id);
}

public interface IDlqRepository
{
    Task InsertAsync(DlqMessage message);
    Task<IEnumerable<DlqRow>> ListUnreplayedAsync(int limit);
    Task<DlqReplayRow?> GetUnreplayedByIdAsync(Guid id);
    Task MarkReplayedAsync(Guid id);
    Task<int> CountUnreplayedAsync();
}

public interface ITenantRepository
{
    Task InsertAsync(string id, string displayName, string status);
    Task SeedIfMissingAsync(string id, string displayName, string status);
    Task<TenantRow?> GetAsync(string id);
    Task<IEnumerable<TenantRow>> ListAsync();
    Task UpdateStatusAsync(string id, string status);
    Task DeleteAsync(string id);
}

public sealed record TenantRow(string Id, string DisplayName, string Status, DateTimeOffset CreatedAt);

public sealed record TableSchema(
    string TableName,
    ColumnSchema KeyColumn,
    IReadOnlyList<ColumnSchema> Columns,
    string? TenantColumn = null);

public sealed record ColumnSchema(string Name, string SqlType, bool IsNullable);
