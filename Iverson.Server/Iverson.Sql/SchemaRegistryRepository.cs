namespace Iverson.Sql;

public sealed class SchemaRegistryRepository(IRecordStoreQueryExecutor sql) : ISchemaRegistryRepository
{
    public Task EnsureTableAsync() =>
        sql.ExecuteAsync(StartupDdl.Serialised("_iverson_schema",
            """
            CREATE TABLE IF NOT EXISTS _iverson_schema (
                type_name  TEXT PRIMARY KEY,
                schema_json JSONB NOT NULL,
                updated_at  TIMESTAMPTZ NOT NULL DEFAULT now()
            )
            """));

    public async Task<IEnumerable<(string TypeName, string SchemaJson)>> LoadAllAsync()
    {
        var rows = await sql.QueryAsync<(string type_name, string schema_json)>(
            "SELECT type_name, schema_json FROM _iverson_schema");
        return rows.Select(r => (r.type_name, r.schema_json));
    }

    public Task UpsertAsync(string typeName, string schemaJson) =>
        sql.ExecuteAsync(
            """
            INSERT INTO _iverson_schema (type_name, schema_json, updated_at)
            VALUES (@TypeName, @Json::jsonb, now())
            ON CONFLICT (type_name) DO UPDATE
                SET schema_json = EXCLUDED.schema_json,
                    updated_at  = EXCLUDED.updated_at
            """,
            new { TypeName = typeName, Json = schemaJson });

    public Task DeleteAsync(string typeName) =>
        sql.ExecuteAsync("DELETE FROM _iverson_schema WHERE type_name = @TypeName", new { TypeName = typeName });

    // SchemaRegistry resolves type names ignoring case, but type_name is a case-sensitive primary
    // key, so a case variant of a stored name is a separate row here. lower() finds every row the
    // registry would fold together. The owner is the ownerTenantId key inside schema_json; ->>
    // gives NULL both for an explicit null and for a legacy row without the key.
    public async Task<IReadOnlyList<string?>> GetOwnersAsync(string typeName) =>
        (await sql.QueryAsync<string?>(
            "SELECT schema_json->>'ownerTenantId' FROM _iverson_schema WHERE lower(type_name) = lower(@TypeName)",
            new { TypeName = typeName })).ToList();

    // The conflict arm only fires for the stored owner: a row of another owner makes this affect
    // zero rows and leaves that row as it was.
    public async Task<bool> UpsertIfOwnerAsync(string typeName, string schemaJson, string? ownerTenantId) =>
        await sql.ExecuteAsync(
            """
            INSERT INTO _iverson_schema (type_name, schema_json, updated_at)
            VALUES (@TypeName, @Json::jsonb, now())
            ON CONFLICT (type_name) DO UPDATE
                SET schema_json = EXCLUDED.schema_json,
                    updated_at  = EXCLUDED.updated_at
                WHERE _iverson_schema.schema_json->>'ownerTenantId' IS NOT DISTINCT FROM @Owner
            """,
            new { TypeName = typeName, Json = schemaJson, Owner = ownerTenantId }) > 0;

    // Narrower than GetOwnersAsync: SchemaBuilder.ToTableName puts '_' before every capital after
    // the first letter, so a case variant of a stored name derives another table, which the stored
    // row never adopted. Only the first letter's case leaves the table unchanged (type names are
    // plain ASCII identifiers, PostgresIdentifier.IsPlain).
    public Task<bool> ExistsAsync(string typeName) =>
        sql.QuerySingleOrDefaultAsync<bool>(
            """
            SELECT EXISTS (SELECT 1 FROM _iverson_schema
                           WHERE lower(type_name) = lower(@TypeName)
                             AND substr(type_name, 2) = substr(@TypeName, 2))
            """,
            new { TypeName = typeName });
}
