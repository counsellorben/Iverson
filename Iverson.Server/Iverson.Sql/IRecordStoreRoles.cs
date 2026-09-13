namespace Iverson.Sql;

/// <summary>
/// Which database role a statement runs under. PostgreSQL exempts a table's OWNER from that
/// table's own RLS policy unless the table is also <c>FORCE ROW LEVEL SECURITY</c>, and exempts a
/// superuser unconditionally — and the app connects as the owner (kubernetes) or as a superuser
/// (docker-compose, Testcontainers). So the connection's ambient role is never a safe place to
/// read a tenant-scoped table from: what filters the rows is the explicit <c>SET LOCAL ROLE</c>
/// below, not the table's RLS state alone.
/// </summary>
public enum RecordStoreRole
{
    /// <summary>
    /// The connection's own role — the table owner. Correct only for the plumbing tables, which
    /// carry no RLS policy: the outbox / reconciliation queue, the DLQ, the tenant registry, the
    /// schema registry and the enrichment state table. Never correct for an entity table: see
    /// <see cref="EntityAccess"/>, which is what <see cref="IEntityRepository"/> takes instead of
    /// this.
    /// <para>
    /// Note that "no RLS policy" is the criterion, not "no grant". Three of those plumbing tables
    /// (reconciliation queue, DLQ, tenant registry) are created through
    /// <c>ApplySchemaAsync</c> and so do carry an <c>iverson_maintenance</c> grant, which that
    /// method issues unconditionally — nothing runs them under the Maintenance role, and the grant
    /// buys them nothing, but it exists. None of them carries an <c>iverson_runtime</c> grant, and
    /// none of them carries a policy, so the connection's own role is the only one that reads them.
    /// </para>
    /// </summary>
    Connection = 0,

    /// <summary>
    /// <c>SET LOCAL ROLE iverson_runtime</c> plus the <c>app.tenant_id</c> GUC, inside one
    /// transaction. <c>iverson_runtime</c> owns nothing and holds no <c>BYPASSRLS</c>, so the
    /// tenant-isolation policy genuinely applies; a null tenant id sets the GUC to NULL and the
    /// policy predicate fails closed to zero rows.
    /// </summary>
    TenantRuntime,

    /// <summary>
    /// <c>SET LOCAL ROLE iverson_maintenance</c>, inside one transaction. That role carries
    /// <c>BYPASSRLS</c>, so this is a deliberate, auditable cross-tenant read or write — the
    /// reconciliation replays and the consumer paths that must re-derive an entity's authoritative
    /// tenant/owner value before any tenant is known. Choosing it is an explicit statement that
    /// the caller intends to see every tenant's rows.
    /// </summary>
    Maintenance
}

public interface IRecordStoreQueryExecutor
{
    Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null, RecordStoreRole role = RecordStoreRole.Connection, string? tenantId = null);
    Task<int> ExecuteAsync(string sql, object? param = null, RecordStoreRole role = RecordStoreRole.Connection, string? tenantId = null);
    Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? param = null, RecordStoreRole role = RecordStoreRole.Connection, string? tenantId = null);
}

public enum SchemaDriftPolicy
{
    /// Log a warning and continue — startup, where a boot failure on historical drift is worse.
    Warn,
    /// Throw — registration, where a RegisterSchema depending on a mis-typed column must fail.
    Throw
}

public sealed class SchemaDriftException(string table, string column, string actual, string expected)
    : Exception($"Column \"{column}\" on table \"{table}\" has type '{actual}' but the registered schema expects '{expected}'. Migrate the column by hand, then retry registration.");

public interface IRecordStoreSchemaManager
{
    Task ApplySchemaAsync(TableSchema schema, SchemaDriftPolicy driftPolicy = SchemaDriftPolicy.Warn);

    /// <summary>
    /// Creates the two non-login roles every entity-table access runs under —
    /// <c>iverson_runtime</c> (RLS-enforced) and <c>iverson_maintenance</c> (<c>BYPASSRLS</c>) —
    /// and verifies the latter really does carry <c>BYPASSRLS</c>. Must run before any
    /// <see cref="ApplySchemaAsync"/> call, since that DDL GRANTs to both.
    /// </summary>
    Task EnsureRolesAsync();
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
/// Enter/exit one of the two explicit entity-table roles for the remainder of an in-flight
/// transaction. <c>SET LOCAL ROLE</c> persists for the rest of the transaction, not just the next
/// statement, so any subsequent statement in the same transaction against a plumbing table with no
/// grant for that role (e.g. the reconciliation/outbox queue for <c>iverson_runtime</c>) must run
/// after <see cref="ExitRoleScopeAsync"/> resets back to the connection's own role. Callers that
/// switch role mid-transaction must always pair it with a reset — hand-duplicating this sequence is
/// exactly how that pairing gets missed.
/// </summary>
public static class TenantScopeTransactionExtensions
{
    public static async Task EnterTenantScopeAsync(this IDbTransactionContext tx, string? tenantId)
    {
        await tx.ExecuteAsync("SET LOCAL ROLE iverson_runtime");
        await tx.ExecuteAsync("SELECT set_config('app.tenant_id', @TenantId, true)", new { TenantId = tenantId });
    }

    /// <summary>
    /// Switches to the <c>BYPASSRLS</c> maintenance role. No <c>app.tenant_id</c> is set: this
    /// scope is cross-tenant on purpose and the GUC would be inert under <c>BYPASSRLS</c> anyway.
    /// </summary>
    public static Task EnterMaintenanceScopeAsync(this IDbTransactionContext tx) =>
        tx.ExecuteAsync("SET LOCAL ROLE iverson_maintenance");

    public static Task ExitRoleScopeAsync(this IDbTransactionContext tx) =>
        tx.ExecuteAsync("RESET ROLE");
}

/// <summary>
/// The boundary an entity-table access sits behind. Every <see cref="IEntityRepository"/> method
/// requires one: there is deliberately no default, because the previous
/// <c>bool tenantScoped = false</c> default meant an omitted flag ran the read on the connection's
/// own role, which — as the table owner, and with no <c>FORCE ROW LEVEL SECURITY</c> — silently
/// returned every tenant's rows instead of failing. A call site now has to answer the question in
/// source: <see cref="ForTenant"/> (RLS-filtered to one tenant) or
/// <see cref="CrossTenantMaintenance"/> (deliberately every tenant).
/// <para>
/// <c>default(EntityAccess)</c> is <c>ForTenant(null)</c> — the fail-closed value, which the RLS
/// predicate resolves to zero rows.
/// </para>
/// </summary>
public readonly record struct EntityAccess
{
    private EntityAccess(bool crossTenant, string? tenantId)
    {
        CrossTenant = crossTenant;
        TenantId    = tenantId;
    }

    /// <summary>True when this access runs under <c>iverson_maintenance</c> (<c>BYPASSRLS</c>).</summary>
    public bool CrossTenant { get; }

    /// <summary>The tenant the RLS GUC is set to. Always null when <see cref="CrossTenant"/>.</summary>
    public string? TenantId { get; }

    /// <summary>
    /// Read/write only the rows belonging to <paramref name="tenantId"/>, enforced by RLS under
    /// <c>iverson_runtime</c>. A null <paramref name="tenantId"/> (e.g. a caller whose token
    /// carries no tenant claim) still switches role and still sets the GUC — to NULL — so the
    /// predicate fails closed to zero rows rather than falling back to an unfiltered read.
    /// </summary>
    public static EntityAccess ForTenant(string? tenantId) => new(false, tenantId);

    /// <summary>
    /// Deliberately cross-tenant, under the <c>BYPASSRLS</c> <c>iverson_maintenance</c> role. Only
    /// for maintenance/reconciliation work and for re-deriving an entity's authoritative tenant or
    /// owner value from the row itself, before any tenant is known. Never reachable from a
    /// tenant's own request path.
    /// </summary>
    public static EntityAccess CrossTenantMaintenance { get; } = new(true, null);

    internal RecordStoreRole Role => CrossTenant ? RecordStoreRole.Maintenance : RecordStoreRole.TenantRuntime;
}

public interface IEntityRepository
{
    Task<string?> FetchByKeyAsync(TableSchema schema, string key, EntityAccess access);
    Task<IEnumerable<KeyedRow>> FetchManyByKeysAsync(TableSchema schema, IReadOnlyList<string> keys, EntityAccess access);
    Task<IEnumerable<string>> FetchByColumnAsync(TableSchema schema, string columnName, string value, EntityAccess access);
    Task<IEnumerable<string>> FetchByArrayContainsAsync(TableSchema schema, string columnName, string value, EntityAccess access);
    Task<IEnumerable<string>> FetchAllAsync(TableSchema schema, EntityAccess access);
    Task<IEnumerable<KeyedTenantRow>> FetchKeysAndTenantsPagedAsync(TableSchema schema, string? afterKey, int pageSize, EntityAccess access);
    Task DeleteAsync(IDbTransactionContext tx, TableSchema schema, string key, EntityAccess access);
    Task UpdateColumnsAsync(IDbTransactionContext tx, TableSchema schema, string key, IReadOnlyDictionary<string, object?> columns, EntityAccess access);
}

public interface IEnrichmentStateRepository
{
    Task EnsureTableAsync();
    Task<string?> GetHashAsync(string tenantId, string typeName, string entityKey);
    Task UpsertAsync(IDbTransactionContext tx, string tenantId, string typeName, string entityKey, string sourceHash, DateTimeOffset enrichedAt);
    Task DeleteAsync(string tenantId, string typeName, string entityKey);
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

public interface IDocumentRerenderQueueRepository
{
    Task EnsureTableAsync();
    Task EnqueueEntityAsync(string? tenantId, string typeName, string entityKey);
    Task EnqueueTypeAsync(string typeName);
    Task<IEnumerable<DocumentRerenderQueueRow>> PollAsync(int maxAttempts, int batchSize);
    Task AdvanceCursorAsync(Guid id, string cursor, DateTime observedEnqueuedAt);
    Task RecordFailureAsync(Guid id, int attempts, string lastError);
    Task DeleteRowAsync(Guid id);
    Task DeleteTypeRowAsync(Guid id, DateTime observedEnqueuedAt);
    Task<int> CountPendingAsync();
    Task<int> CountExhaustedAsync(int maxAttempts);
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

/// <param name="CreatedAt">
/// <see cref="DateTime"/>, not <see cref="DateTimeOffset"/>: the column is
/// <c>timestamp with time zone</c>, which Npgsql materializes as a UTC <see cref="DateTime"/>.
/// Dapper matches a record's constructor by parameter type, so declaring this as
/// <see cref="DateTimeOffset"/> made every <c>GetAsync</c>/<c>ListAsync</c> throw
/// "a parameterless default constructor or one matching signature ... is required" at runtime.
/// </param>
public sealed record TenantRow(string Id, string DisplayName, string Status, DateTime CreatedAt);

public sealed record TableSchema(
    string TableName,
    ColumnSchema KeyColumn,
    IReadOnlyList<ColumnSchema> Columns,
    string? TenantColumn = null);

public sealed record ColumnSchema(string Name, string SqlType, bool IsNullable);
