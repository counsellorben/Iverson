namespace Iverson.Sql;

/// <summary>What sits in <c>public</c> under a type's derived table name.</summary>
/// <param name="IsOrdinaryTable"><c>relkind = 'r'</c>: not a view, sequence, index or partitioned table.</param>
public sealed record ExistingTable(bool IsOrdinaryTable, bool HasTenantColumn);

/// <summary>
/// RegisterSchema's adoption check (CSR round 12 §5.3 step 2), for a type with no registry row
/// whose derived table already exists.
/// </summary>
public sealed class ExistingTableInspector(IRecordStoreQueryExecutor sql) : IExistingTableInspector
{
    public Task<ExistingTable?> DescribeAsync(string tableName, string tenantColumn) =>
        sql.QuerySingleOrDefaultAsync<ExistingTable>(
            """
            SELECT c.relkind = 'r' AS "IsOrdinaryTable",
                   EXISTS (SELECT 1 FROM pg_attribute a
                           WHERE a.attrelid = c.oid AND a.attname = @TenantColumn
                             AND a.attnum > 0 AND NOT a.attisdropped) AS "HasTenantColumn"
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relname = @TableName
            """,
            new { TableName = tableName, TenantColumn = tenantColumn });

    // Under iverson_maintenance (BYPASSRLS): the check must see every tenant's rows. A NULL tenant
    // value counts as another tenant's. The predicate means IS DISTINCT FROM @Owner, spelled as two
    // ranges and a NULL test so that the tenant column's index can answer it; IS DISTINCT FROM
    // cannot use an index, so it read the whole table.
    public Task<bool> HasRowsOfAnotherTenantAsync(string tableName, string tenantColumn, string ownerTenantId)
    {
        var column = PostgresIdentifier.Quote(tenantColumn);
        return sql.QuerySingleOrDefaultAsync<bool>(
            $"""
            SELECT EXISTS (SELECT 1 FROM {PostgresIdentifier.Quote(tableName)}
                           WHERE {column} < @Owner OR {column} > @Owner OR {column} IS NULL)
            """,
            new { Owner = ownerTenantId },
            RecordStoreRole.Maintenance);
    }
}
