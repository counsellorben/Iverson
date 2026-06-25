using System.Collections.Concurrent;
using System.Text.Json;
using Iverson.Sql;
using Microsoft.Extensions.Logging;

namespace Iverson.Api.Schema;

public sealed class SchemaRegistry(IPostgresRepository sql, ILogger<SchemaRegistry> logger)
{
    private readonly ConcurrentDictionary<string, SchemaDescriptor> _schemas = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, SchemaDescriptor> All => _schemas;

    public SchemaDescriptor? Get(string typeName)
        => _schemas.TryGetValue(typeName, out var s) ? s : null;

    public bool IsRegistered(string typeName) => _schemas.ContainsKey(typeName);

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await EnsureMetadataTableAsync();

        var rows = await sql.QueryAsync<(string type_name, string schema_json)>(
            "SELECT type_name, schema_json FROM _iverson_schema");

        foreach (var (typeName, json) in rows)
        {
            var descriptor = JsonSerializer.Deserialize<SchemaDescriptor>(json, s_jsonOptions);
            if (descriptor is not null)
                _schemas[typeName] = descriptor;
        }

        logger.LogInformation("SchemaRegistry loaded {Count} schema(s)", _schemas.Count);
    }

    public async Task RegisterAsync(SchemaDescriptor descriptor)
    {
        await EnsureMetadataTableAsync();

        var json = JsonSerializer.Serialize(descriptor, s_jsonOptions);

        await sql.ExecuteAsync(
            """
            INSERT INTO _iverson_schema (type_name, schema_json, updated_at)
            VALUES (@TypeName, @Json::jsonb, now())
            ON CONFLICT (type_name) DO UPDATE
                SET schema_json = EXCLUDED.schema_json,
                    updated_at  = EXCLUDED.updated_at
            """,
            new { TypeName = descriptor.TypeName, Json = json });

        _schemas[descriptor.TypeName] = descriptor;
        logger.LogInformation("Registered schema for {TypeName}", descriptor.TypeName);
    }

    public async Task UnregisterAsync(string typeName)
    {
        await sql.ExecuteAsync(
            "DELETE FROM _iverson_schema WHERE type_name = @TypeName",
            new { TypeName = typeName });

        _schemas.TryRemove(typeName, out _);
        logger.LogInformation("Unregistered schema for {TypeName}", typeName);
    }

    private async Task EnsureMetadataTableAsync()
    {
        await sql.ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS _iverson_schema (
                type_name  TEXT PRIMARY KEY,
                schema_json JSONB NOT NULL,
                updated_at  TIMESTAMPTZ NOT NULL DEFAULT now()
            )
            """);
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        WriteIndented               = false
    };
}
