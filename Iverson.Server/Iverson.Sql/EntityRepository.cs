using Dapper;

namespace Iverson.Sql;

// Every table and column name in these statements comes from a registered schema, so each one goes
// through PostgresIdentifier.Quote; values always travel as parameters.
public sealed class EntityRepository(IRecordStoreQueryExecutor sql) : IEntityRepository
{
    public Task<string?> FetchByKeyAsync(TableSchema schema, string key, EntityAccess access)
    {
        var table     = PostgresIdentifier.Quote(schema.TableName);
        var keyColumn = PostgresIdentifier.Quote(schema.KeyColumn.Name);
        return sql.QuerySingleOrDefaultAsync<string>(
            $"SELECT row_to_json(t)::text FROM {table} t WHERE {keyColumn} = @Key::uuid",
            new { Key = key }, access.Role, access.TenantId);
    }

    public Task<IEnumerable<KeyedRow>> FetchManyByKeysAsync(TableSchema schema, IReadOnlyList<string> keys, EntityAccess access)
    {
        var table     = PostgresIdentifier.Quote(schema.TableName);
        var keyColumn = PostgresIdentifier.Quote(schema.KeyColumn.Name);

        // Guid[], not string[]: Npgsql sends string[] as text[], which blocks Postgres from using
        // the uuid primary key index for ANY(...) — see this plan's Global Constraints.
        var keyGuids = keys.Select(Guid.Parse).ToArray();
        return sql.QueryAsync<KeyedRow>(
            $"SELECT {keyColumn}::text AS key, row_to_json(t)::text AS data " +
            $"FROM {table} t " +
            $"WHERE {keyColumn} = ANY(@Keys)",
            new { Keys = keyGuids }, access.Role, access.TenantId);
    }

    public Task<IEnumerable<string>> FetchByColumnAsync(TableSchema schema, string columnName, string value, EntityAccess access)
    {
        var table  = PostgresIdentifier.Quote(schema.TableName);
        var column = PostgresIdentifier.Quote(columnName);
        return sql.QueryAsync<string>(
            $"SELECT row_to_json(t)::text FROM {table} t WHERE {column} = @Key::uuid",
            new { Key = value }, access.Role, access.TenantId);
    }

    public Task<IEnumerable<string>> FetchAllAsync(TableSchema schema, EntityAccess access) =>
        sql.QueryAsync<string>(
            $"SELECT row_to_json(t)::text FROM {PostgresIdentifier.Quote(schema.TableName)} t",
            null, access.Role, access.TenantId);

    // A type-level re-render row means "every entity of this type, across every tenant", so its
    // one caller passes EntityAccess.CrossTenantMaintenance — scoping this to one tenant would
    // silently backfill only that tenant. The parameter is still required rather than hard-wired:
    // the exemption belongs at the call site, where a reviewer reads it. Keyset pagination ordered
    // by the key column, not OFFSET, so this stays stable and cheap as the table grows.
    public Task<IEnumerable<KeyedTenantRow>> FetchKeysAndTenantsPagedAsync(TableSchema schema, string? afterKey, int pageSize, EntityAccess access)
    {
        var table     = PostgresIdentifier.Quote(schema.TableName);
        var keyColumn = PostgresIdentifier.Quote(schema.KeyColumn.Name);
        var tenantSelect = schema.TenantColumn is not null
            ? $"{PostgresIdentifier.Quote(schema.TenantColumn)}::text AS \"TenantId\""
            : "NULL AS \"TenantId\"";

        if (afterKey is null)
        {
            return sql.QueryAsync<KeyedTenantRow>(
                $"""
                SELECT {keyColumn}::text AS "Key", {tenantSelect}
                FROM {table}
                ORDER BY {keyColumn}
                LIMIT @PageSize
                """,
                new { PageSize = pageSize }, access.Role, access.TenantId);
        }

        return sql.QueryAsync<KeyedTenantRow>(
            $"""
            SELECT {keyColumn}::text AS "Key", {tenantSelect}
            FROM {table}
            WHERE {keyColumn} > @AfterKey::uuid
            ORDER BY {keyColumn}
            LIMIT @PageSize
            """,
            new { AfterKey = afterKey, PageSize = pageSize }, access.Role, access.TenantId);
    }

    public async Task DeleteAsync(IDbTransactionContext tx, TableSchema schema, string key, EntityAccess access)
    {
        // Quoted before the role switch, so a refused name leaves no role scope behind.
        var table     = PostgresIdentifier.Quote(schema.TableName);
        var keyColumn = PostgresIdentifier.Quote(schema.KeyColumn.Name);

        if (access.CrossTenant)
            await tx.EnterMaintenanceScopeAsync();
        else
            await tx.EnterTenantScopeAsync(access.TenantId);

        await tx.ExecuteAsync(
            $"DELETE FROM {table} WHERE {keyColumn} = @Key::uuid",
            new { Key = key });

        // SET LOCAL ROLE persists for the rest of the transaction, not just this statement.
        // Callers (e.g. ObjectMappingGrpcService.Delete) go on to write to plumbing tables
        // (the reconciliation/outbox queue) in this same transaction, and neither entity role has
        // a grant on those — reset back to the connection's own role before returning.
        await tx.ExitRoleScopeAsync();
    }

    // Enters and exits the role itself, exactly like DeleteAsync above. Its one caller
    // (EnrichmentConsumer) used to hand-roll the EnterTenantScopeAsync/ExitRoleScopeAsync pair
    // around this call — the very duplication TenantScopeTransactionExtensions' doc comment warns
    // is how the pairing gets missed — and leaving this method as the only IEntityRepository
    // member that does not name its role would have left a future caller one forgotten Enter away
    // from the silent unscoped write this batch exists to prevent.
    public async Task UpdateColumnsAsync(
        IDbTransactionContext tx, TableSchema schema, string key,
        IReadOnlyDictionary<string, object?> columns, EntityAccess access)
    {
        // Each column name is also its parameter name (@Title), and a parameter name cannot be
        // quoted, so it must have the plain identifier shape. The only caller's keys are
        // enrichment target property names, which registration already holds to that shape.
        foreach (var column in columns.Keys)
        {
            if (!PostgresIdentifier.IsPlain(column))
                throw new ArgumentException(
                    $"Column '{column}' cannot be bound as a parameter: a parameter name must be a letter followed by letters and digits.",
                    nameof(columns));
        }

        var table     = PostgresIdentifier.Quote(schema.TableName);
        var keyColumn = PostgresIdentifier.Quote(schema.KeyColumn.Name);
        var setClause = string.Join(", ", columns.Keys.Select(c => $"{PostgresIdentifier.Quote(c)} = @{c}"));
        var parameters = new DynamicParameters();
        foreach (var (column, value) in columns)
        {
            parameters.Add(column, value);
        }
        parameters.Add("Key", key);

        if (access.CrossTenant)
            await tx.EnterMaintenanceScopeAsync();
        else
            await tx.EnterTenantScopeAsync(access.TenantId);

        await tx.ExecuteAsync(
            $"UPDATE {table} SET {setClause} WHERE {keyColumn} = @Key::uuid",
            parameters);

        // SET LOCAL ROLE persists for the rest of the transaction. EnrichmentConsumer goes on to
        // write the enrichment-state row and the outbox row in this same transaction, and neither
        // entity role has a grant on those plumbing tables — reset before returning.
        await tx.ExitRoleScopeAsync();
    }
}
