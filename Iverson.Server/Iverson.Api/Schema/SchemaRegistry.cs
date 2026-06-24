using System.Collections.Concurrent;
using System.Text.Json;
using Iverson.Sql;
using Microsoft.Extensions.Logging;

namespace Iverson.Api.Schema;

public sealed class SchemaRegistry(IPostgresRepository sql, ILogger<SchemaRegistry> logger)
{
    private readonly ConcurrentDictionary<string, SchemaDescriptor> _schemas = new(StringComparer.OrdinalIgnoreCase);

    // typeName → set of ES-eligible schema TypeNames that embed it as a relation
    private readonly ConcurrentDictionary<string, HashSet<string>> _inverseIndex = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, SchemaDescriptor> All => _schemas;

    public SchemaDescriptor? Get(string typeName)
        => _schemas.TryGetValue(typeName, out var s) ? s : null;

    public bool IsRegistered(string typeName) => _schemas.ContainsKey(typeName);

    /// <summary>
    /// Returns true if any registered ES-eligible schema embeds <paramref name="typeName"/> as a relation,
    /// directly or transitively. Used to set <see cref="Iverson.Events.StoreTarget.EngagementFanout"/>.
    /// </summary>
    public bool HasEngagementDependents(string typeName)
        => _inverseIndex.ContainsKey(typeName);

    /// <summary>
    /// Returns all ES-eligible schemas that directly embed <paramref name="typeName"/> as a relation.
    /// </summary>
    public IReadOnlyList<SchemaDescriptor> GetDirectEngagementDependents(string typeName)
    {
        if (!_inverseIndex.TryGetValue(typeName, out var dependentNames))
            return [];

        return dependentNames
            .Select(n => _schemas.TryGetValue(n, out var s) ? s : null)
            .OfType<SchemaDescriptor>()
            .ToList();
    }

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

        RebuildInverseIndex();
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
        RebuildInverseIndex();
        logger.LogInformation("Registered schema for {TypeName}", descriptor.TypeName);
    }

    public async Task UnregisterAsync(string typeName)
    {
        await sql.ExecuteAsync(
            "DELETE FROM _iverson_schema WHERE type_name = @TypeName",
            new { TypeName = typeName });

        _schemas.TryRemove(typeName, out _);
        RebuildInverseIndex();
        logger.LogInformation("Unregistered schema for {TypeName}", typeName);
    }

    // Rebuilds the full inverse index from current schema state.
    // Only ES-eligible schemas (IsCompleteForIngestion) participate as dependents.
    private void RebuildInverseIndex()
    {
        _inverseIndex.Clear();

        var esEligible = _schemas.Values
            .Where(IsCompleteForEngagement)
            .ToList();

        foreach (var schema in esEligible)
        {
            foreach (var relation in schema.Relations)
            {
                _inverseIndex
                    .GetOrAdd(relation.RelatedTypeName, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                    .Add(schema.TypeName);
            }
        }

        // Propagate transitively: if A → B and B → C, then C should also fan out to A.
        bool changed;
        do
        {
            changed = false;
            foreach (var (dependency, dependents) in _inverseIndex.ToList())
            {
                if (!_inverseIndex.TryGetValue(dependency, out var upstreamDependents))
                    continue;

                foreach (var upstream in upstreamDependents)
                {
                    var set = _inverseIndex.GetOrAdd(dependency, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    foreach (var d in dependents)
                        changed |= set.Add(upstream);
                }
            }
        } while (changed);
    }

    private static bool IsCompleteForEngagement(SchemaDescriptor schema) =>
        schema.Relations.All(r => r.Kind switch
        {
            RelationKind.ManyToOne  => true,
            RelationKind.OneToOne   => true,
            RelationKind.OneToMany  => false,
            RelationKind.ManyToMany => schema.FkColumns.Any(fk =>
                string.Equals(fk.ColumnName, r.ForeignKey, StringComparison.OrdinalIgnoreCase)),
            _                       => false
        });

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
