namespace Iverson.Sql;

public sealed class SchemaRegistryRepository(IRecordStoreQueryExecutor sql) : ISchemaRegistryRepository
{
    public Task EnsureTableAsync() =>
        sql.ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS _iverson_schema (
                type_name  TEXT PRIMARY KEY,
                schema_json JSONB NOT NULL,
                updated_at  TIMESTAMPTZ NOT NULL DEFAULT now()
            )
            """);

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
}
