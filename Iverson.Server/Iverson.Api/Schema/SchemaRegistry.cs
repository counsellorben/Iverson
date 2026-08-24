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

    // Serialises the on-demand reloads below and throttles them. Without the throttle a backlog
    // of genuinely-unknown-type messages would issue one Postgres round trip per delivery
    // attempt; with it, a burst collapses to one reload per interval and every waiter re-reads
    // the map the winner published.
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private DateTimeOffset _lastReload = DateTimeOffset.MinValue;
    private static readonly TimeSpan ReloadThrottle = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The descriptor for <paramref name="typeName"/>, forcing at most one registry reload when it
    /// is not already cached.
    ///
    /// This closes the cold-registry race: <c>SchemaRefreshWorker</c> polls every 30 s, so a type
    /// registered moments before its first write is invisible to a consumer that only consults the
    /// cached map. The projection consumers used to log an Error and RETURN on a cache miss, which
    /// completes the Kafka handler normally and therefore COMMITS the offset — a terminal, silent
    /// loss. Reloading here converts the common case (the type really is registered, we just have
    /// not polled yet) into a hit; a still-missing type is left for the caller to throw on, so the
    /// delivery contract's bounded retry and dead-letter apply instead of a silent drop.
    /// </summary>
    public async Task<SchemaDescriptor?> GetOrReloadAsync(string typeName, CancellationToken ct = default)
    {
        var cached = Get(typeName);
        if (cached is not null) return cached;

        await _reloadGate.WaitAsync(ct);
        try
        {
            // Re-check under the gate: while we waited, another caller may have reloaded and
            // published the very descriptor we are after.
            cached = Get(typeName);
            if (cached is not null) return cached;

            if (DateTimeOffset.UtcNow - _lastReload < ReloadThrottle) return null;

            await LoadAsync(ct);
            _lastReload = DateTimeOffset.UtcNow;
        }
        finally
        {
            _reloadGate.Release();
        }

        return Get(typeName);
    }

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
            // ORDER IS LOAD-BEARING: this runs BEFORE the try below, so a row that Postgres
            // still returns counts as "present" even when it fails to deserialize. That is the
            // only reason the skip-and-log containment argument holds. The reconcile loop at the
            // bottom of this method evicts every cached schema NOT in this set, so if the Add were
            // moved into the try (or after it), a row that becomes corrupt after a successful boot
            // would be missing from the set on the next 30 s SchemaRefreshWorker poll and the
            // reconcile loop would EVICT the already-good in-memory descriptor — silently
            // unregistering a live type on a RUNNING instance, with the terminal write loss
            // described at the catch below. Keeping the Add here confines that degradation to a
            // cold boot or a fresh replica, which is what makes it survivable at all.
            loadedTypeNames.Add(typeName);

            // THE deserialization boundary. Everything downstream treats SchemaDescriptor's
            // non-nullable members as real values; this is the only place that can make that true,
            // because System.Text.Json erases nullable reference-type annotations and a row written
            // before the tenant column existed (63a577a, 2026-07-17) carries no `tenantColumn` key.
            // A malformed or legacy row is SKIPPED, not fatal: refusing to boot — or, on the
            // SchemaRefreshWorker's 30 s poll, throwing out of the whole loop and freezing every
            // OTHER schema's refresh — would take down a running deployment over one bad row, which
            // is worse than the writes it enables. A skipped type is simply not registered, and
            // every RPC and consumer already fails closed on an unregistered type.
            SchemaDescriptor? descriptor;
            try
            {
                descriptor = JsonSerializer.Deserialize<SchemaDescriptor>(json, s_jsonOptions);
            }
            catch (JsonException ex)
            {
                // THE COST OF SKIPPING, stated so nobody reads this catch as free. On a COLD BOOT
                // or a FRESH REPLICA the type is simply never registered, so every Kafka write for
                // it hits the consumers' unknown-type arm (EngagementStoreConsumer,
                // IntelligenceStoreConsumer, EnrichmentConsumer) for the life of that replica.
                //
                // Those arms used to log an Error and RETURN, which COMMITS THE KAFKA OFFSET and
                // made the drop TERMINAL. They now call GetOrReloadAsync and THROW when the type is
                // still unknown after a forced reload, so the offset is not committed and the
                // message goes to the DLQ via MessageDispatcher's bounded retry. A corrupt row is
                // therefore no longer silent data loss — but it is not harmless either: the
                // registry can never admit this row, so every retry fails and every message for the
                // type dead-letters until someone re-registers it. The Error below is still the
                // first warning anyone gets, and now the DLQ is the second.
                //
                // Skipping remains the better trade than throwing out of this loop (which would
                // freeze every OTHER schema's 30 s refresh, and present as "the registry stopped
                // updating" rather than a named row), and the Add above keeps a running instance
                // out of it entirely.
                //
                // The message names the tenant column as the EXPECTED cause without asserting it:
                // any malformed field in the row lands here, and `ex` is passed so the real
                // System.Text.Json message renders alongside.
                logger.LogError(ex,
                    "Schema '{TypeName}' could not be deserialized from storage and was NOT loaded. " +
                    "The usual cause is a row predating the tenant boundary, which carries no " +
                    "`tenantColumn` key and surfaces as \"missing required properties including: " +
                    "'tenantColumn'\" — but ANY malformed field in the row reaches this handler, so " +
                    "read the deserializer message logged BELOW this line before assuming. " +
                    "Re-register the type to repair it.",
                    typeName);
                continue;
            }

            if (descriptor is not null)
            {
                // `required` only guarantees the KEY was present — an explicit
                // "tenantColumn": null still deserializes to null. This is the check that makes
                // SchemaDescriptor.TenantColumn's non-nullable annotation a runtime fact.
                if (string.IsNullOrEmpty(descriptor.TenantColumn))
                {
                    logger.LogError(
                        "Schema '{TypeName}' loaded from storage carries no server-owned tenant " +
                        "column and was NOT registered. It predates the tenant boundary; every " +
                        "read, write and projection for this type fails closed until it is " +
                        "re-registered.",
                        typeName);
                    continue;
                }

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
        // This loop is the reason `loadedTypeNames.Add` sits OUTSIDE the try above: "not in the
        // set" means EVICT, so a row that merely failed to deserialize must still count as
        // present, or a corruption appearing after boot would unregister a live type mid-flight.
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
                var relation = schema.Relations.FirstOrDefault(
                    r => string.Equals(r.PropertyName, relationName, StringComparison.OrdinalIgnoreCase));
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
