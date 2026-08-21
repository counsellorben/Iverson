using System.Collections.Concurrent;
using System.Text.Json;
using Iverson.Sql;

namespace Iverson.Api.Schema;

public sealed class SchemaRegistry(
    ISchemaRegistryRepository repository,
    ILogger<SchemaRegistry> logger)
{
    private readonly ConcurrentDictionary<string, SchemaDescriptor> _schemas = new(StringComparer.OrdinalIgnoreCase);

    // Reverse-dependency index: target type name -> the (declaring type, relation) pairs whose
    // registered document template references that type through the relation. Rebuilt wholesale
    // (not incrementally patched) at both mutation points below, since a template on ANY schema
    // can reference ANY other type — a single RegisterAsync call can change which entries every
    // other type's dependents list needs, not just entries keyed on the registered type itself.
    private volatile IReadOnlyDictionary<string, List<(string DeclaringType, RelationDescriptor Relation)>> _reverseIndex =
        new Dictionary<string, List<(string, RelationDescriptor)>>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, SchemaDescriptor> All => _schemas;

    public SchemaDescriptor? Get(string typeName) =>
        _schemas.TryGetValue(typeName, out var s) ? s : null;

    public bool IsRegistered(string typeName) => _schemas.ContainsKey(typeName);

    /// <summary>
    /// The (declaringType, relation) pairs whose document template references <paramref name="typeName"/>
    /// — i.e. whose rendered document must be re-rendered when an entity of <paramref name="typeName"/>
    /// changes. Consumed by <c>DocumentRerenderConsumer</c> (T7).
    /// </summary>
    public IReadOnlyList<(string DeclaringType, RelationDescriptor Relation)> GetDependents(string typeName) =>
        _reverseIndex.TryGetValue(typeName, out var deps) ? deps : [];

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
                _schemas[typeName] = descriptor;
        }

        // Reconcile removals: a schema present in this instance's cache but no longer
        // returned by Postgres was unregistered by a different process (e.g. a different
        // api/worker replica calling UnregisterAsync) — without this, a periodic re-poll
        // could never converge on that removal.
        foreach (var typeName in _schemas.Keys)
            if (!loadedTypeNames.Contains(typeName))
                _schemas.TryRemove(typeName, out _);

        RebuildReverseIndex();

        logger.LogInformation("SchemaRegistry loaded {Count} schema(s)", _schemas.Count);
    }

    public async Task RegisterAsync(SchemaDescriptor descriptor)
    {
        await repository.EnsureTableAsync();

        var json = JsonSerializer.Serialize(descriptor, s_jsonOptions);
        await repository.UpsertAsync(descriptor.TypeName, json);

        _schemas[descriptor.TypeName] = descriptor;
        RebuildReverseIndex();
        logger.LogInformation("Registered schema for {TypeName}", descriptor.TypeName);
    }

    public async Task UnregisterAsync(string typeName)
    {
        await repository.DeleteAsync(typeName);

        _schemas.TryRemove(typeName, out _);
        logger.LogInformation("Unregistered schema for {TypeName}", typeName);
    }

    // Derived from schema.DocumentTemplate.Segments, not from schema.Relations wholesale — a
    // relation the template never references contributes no dependency, even though it still
    // exists on the schema. Only top-level segments carry a RelationName that maps to a
    // Relations entry (a Block's Inner segments are Literal/Scalar only, per DocumentSegment's
    // doc comment), so no recursion into Inner is needed.
    private void RebuildReverseIndex()
    {
        var index = new Dictionary<string, List<(string, RelationDescriptor)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var schema in _schemas.Values)
        {
            var template = schema.DocumentTemplate;
            if (template is null) continue;

            var relationNames = template.Segments
                .Where(s => s.RelationName is not null)
                .Select(s => s.RelationName!)
                .Distinct(StringComparer.Ordinal);

            foreach (var relationName in relationNames)
            {
                var relation = schema.Relations.FirstOrDefault(r => r.PropertyName == relationName);
                if (relation is null) continue;

                if (!index.TryGetValue(relation.RelatedTypeName, out var deps))
                    index[relation.RelatedTypeName] = deps = [];

                deps.Add((schema.TypeName, relation));
            }
        }

        _reverseIndex = index;
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        WriteIndented               = false
    };
}
