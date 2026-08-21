using Dapper;

namespace Iverson.Sql;

public sealed class EntityRepository(IRecordStoreQueryExecutor sql) : IEntityRepository
{
    public Task<string?> FetchByKeyAsync(TableSchema schema, string key, bool tenantScoped = false, string? tenantId = null) =>
        sql.QuerySingleOrDefaultAsync<string>(
            $"SELECT row_to_json(t)::text FROM \"{schema.TableName}\" t WHERE \"{schema.KeyColumn.Name}\" = @Key::uuid",
            new { Key = key }, tenantScoped, tenantId);

    public Task<IEnumerable<KeyedRow>> FetchManyByKeysAsync(TableSchema schema, IReadOnlyList<string> keys, bool tenantScoped = false, string? tenantId = null)
    {
        // Guid[], not string[]: Npgsql sends string[] as text[], which blocks Postgres from using
        // the uuid primary key index for ANY(...) — see this plan's Global Constraints.
        var keyGuids = keys.Select(Guid.Parse).ToArray();
        return sql.QueryAsync<KeyedRow>(
            $"SELECT \"{schema.KeyColumn.Name}\"::text AS key, row_to_json(t)::text AS data " +
            $"FROM \"{schema.TableName}\" t " +
            $"WHERE \"{schema.KeyColumn.Name}\" = ANY(@Keys)",
            new { Keys = keyGuids }, tenantScoped, tenantId);
    }

    public Task<IEnumerable<string>> FetchByColumnAsync(TableSchema schema, string columnName, string value, bool tenantScoped = false, string? tenantId = null) =>
        sql.QueryAsync<string>(
            $"SELECT row_to_json(t)::text FROM \"{schema.TableName}\" t WHERE \"{columnName}\" = @Key::uuid",
            new { Key = value }, tenantScoped, tenantId);

    public Task<IEnumerable<string>> FetchAllAsync(TableSchema schema, bool tenantScoped = false, string? tenantId = null) =>
        sql.QueryAsync<string>($"""SELECT row_to_json(t)::text FROM "{schema.TableName}" t""", null, tenantScoped, tenantId);

    // Unscoped (like FetchAllAsync) — a type-level re-render row means "every entity of this
    // type, across every tenant", so scoping this to one tenant would silently backfill only
    // that tenant. Keyset pagination ordered by the key column, not OFFSET, so this stays stable
    // and cheap as the table grows.
    public Task<IEnumerable<KeyedTenantRow>> FetchKeysAndTenantsPagedAsync(TableSchema schema, string? afterKey, int pageSize)
    {
        var tenantSelect = schema.TenantColumn is not null
            ? $"\"{schema.TenantColumn}\"::text AS \"TenantId\""
            : "NULL AS \"TenantId\"";

        if (afterKey is null)
        {
            return sql.QueryAsync<KeyedTenantRow>(
                $"""
                SELECT "{schema.KeyColumn.Name}"::text AS "Key", {tenantSelect}
                FROM "{schema.TableName}"
                ORDER BY "{schema.KeyColumn.Name}"
                LIMIT @PageSize
                """,
                new { PageSize = pageSize });
        }

        return sql.QueryAsync<KeyedTenantRow>(
            $"""
            SELECT "{schema.KeyColumn.Name}"::text AS "Key", {tenantSelect}
            FROM "{schema.TableName}"
            WHERE "{schema.KeyColumn.Name}" > @AfterKey::uuid
            ORDER BY "{schema.KeyColumn.Name}"
            LIMIT @PageSize
            """,
            new { AfterKey = afterKey, PageSize = pageSize });
    }

    public async Task DeleteAsync(IDbTransactionContext tx, TableSchema schema, string key, bool tenantScoped = false, string? tenantId = null)
    {
        if (tenantScoped)
        {
            await tx.EnterTenantScopeAsync(tenantId);
        }
        await tx.ExecuteAsync(
            $"DELETE FROM \"{schema.TableName}\" WHERE \"{schema.KeyColumn.Name}\" = @Key::uuid",
            new { Key = key });
        if (tenantScoped)
        {
            // SET LOCAL ROLE persists for the rest of the transaction, not just this statement.
            // Callers (e.g. ObjectMappingGrpcService.Delete) go on to write to plumbing tables
            // (the reconciliation/outbox queue) in this same transaction, and iverson_runtime has
            // no grant on those — reset back to the superuser role before returning.
            await tx.ExitTenantScopeAsync();
        }
    }

    public Task UpdateColumnsAsync(
        IDbTransactionContext tx, TableSchema schema, string key,
        IReadOnlyDictionary<string, object?> columns)
    {
        var setClause = string.Join(", ", columns.Keys.Select(c => $"\"{c}\" = @{c}"));
        var parameters = new DynamicParameters();
        foreach (var (column, value) in columns)
        {
            parameters.Add(column, value);
        }
        parameters.Add("Key", key);

        return tx.ExecuteAsync(
            $"UPDATE \"{schema.TableName}\" SET {setClause} WHERE \"{schema.KeyColumn.Name}\" = @Key::uuid",
            parameters);
    }
}
