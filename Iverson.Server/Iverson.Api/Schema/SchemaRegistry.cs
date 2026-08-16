using System.Collections.Concurrent;
using System.Text.Json;
using Iverson.Sql;

namespace Iverson.Api.Schema;

public sealed class SchemaRegistry(
    ISchemaRegistryRepository repository,
    ILogger<SchemaRegistry> logger)
{
    private readonly ConcurrentDictionary<string, SchemaDescriptor> _schemas = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, SchemaDescriptor> All => _schemas;

    public SchemaDescriptor? Get(string typeName) =>
        _schemas.TryGetValue(typeName, out var s) ? s : null;

    public bool IsRegistered(string typeName) => _schemas.ContainsKey(typeName);

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await repository.EnsureTableAsync();

        var rows = await repository.LoadAllAsync();

        var loadedTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (typeName, json) in rows)
        {
            loadedTypeNames.Add(typeName);
            var descriptor = JsonSerializer.Deserialize<SchemaDescriptor>(json, s_jsonOptions);
            if (descriptor is not null)
            {
                // Rehydration bypasses SchemaRegistrationOrchestrator, so a descriptor persisted
                // before the collision check existed (or otherwise corrupted) can still be sitting
                // in Postgres. We do NOT refuse to boot on it — failing startup on a legacy schema
                // would take down a running deployment, which is worse than the bad writes it
                // enables. Instead, flag it loudly so it gets re-registered: every Create/Update
                // against it will fail RelationValidator anyway.
                foreach (var relation in descriptor.Relations)
                {
                    if (RelationCollisionCheck.IsCollision(relation))
                    {
                        logger.LogError(
                            "Schema '{TypeName}' loaded from storage has relation '{PropertyName}' whose " +
                            "navigation-property name is identical to its foreign key '{ForeignKey}'. This " +
                            "schema predates (or otherwise bypassed) the registration-time collision check " +
                            "and must be re-registered with a distinct navigation property name.",
                            typeName, relation.PropertyName, relation.ForeignKey);
                    }
                }

                _schemas[typeName] = descriptor;
            }
        }

        // Reconcile removals: a schema present in this instance's cache but no longer
        // returned by Postgres was unregistered by a different process (e.g. a different
        // api/worker replica calling UnregisterAsync) — without this, a periodic re-poll
        // could never converge on that removal.
        foreach (var typeName in _schemas.Keys)
            if (!loadedTypeNames.Contains(typeName))
                _schemas.TryRemove(typeName, out _);

        logger.LogInformation("SchemaRegistry loaded {Count} schema(s)", _schemas.Count);
    }

    public async Task RegisterAsync(SchemaDescriptor descriptor)
    {
        await repository.EnsureTableAsync();

        var json = JsonSerializer.Serialize(descriptor, s_jsonOptions);
        await repository.UpsertAsync(descriptor.TypeName, json);

        _schemas[descriptor.TypeName] = descriptor;
        logger.LogInformation("Registered schema for {TypeName}", descriptor.TypeName);
    }

    public async Task UnregisterAsync(string typeName)
    {
        await repository.DeleteAsync(typeName);

        _schemas.TryRemove(typeName, out _);
        logger.LogInformation("Unregistered schema for {TypeName}", typeName);
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        WriteIndented               = false
    };
}
