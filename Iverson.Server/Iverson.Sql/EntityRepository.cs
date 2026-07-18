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

    public async Task DeleteAsync(IDbTransactionContext tx, TableSchema schema, string key, bool tenantScoped = false, string? tenantId = null)
    {
        if (tenantScoped)
        {
            await tx.ExecuteAsync("SET LOCAL ROLE iverson_runtime");
            await tx.ExecuteAsync("SELECT set_config('app.tenant_id', @TenantId, true)", new { TenantId = tenantId });
        }
        await tx.ExecuteAsync(
            $"DELETE FROM \"{schema.TableName}\" WHERE \"{schema.KeyColumn.Name}\" = @Key::uuid",
            new { Key = key });
    }
}
