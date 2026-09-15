using System.Text.Json;
using Grpc.Core;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Events;
using Iverson.Sql;
using Iverson.StarRocks;
using Iverson.Vector;
using Microsoft.Extensions.Options;
using Qdrant.Client;

// The proto (Iverson.Client.Contracts) and StarRocks namespaces both declare types named
// AggregationResult, AggregationBucket, and RelationDescriptor — importing both namespaces makes
// the bare names ambiguous. These aliases resolve them the same way existing call sites already
// do: ObjectSearchGrpcService.cs / ObjectSearchGrpcServiceTests.cs alias AggregationResult to
// EngagementAggResult/SrAggResult and AggregationBucket to SrAggBucket; EntityRelationResolver.cs
// and ServerOwnedTenantColumnTests.cs alias RelationDescriptor to SchemaRelationDescriptor.
using EngagementAggResult = Iverson.StarRocks.AggregationResult;
using SrAggBucket = Iverson.StarRocks.AggregationBucket;
using SchemaRelationDescriptor = Iverson.Api.Schema.RelationDescriptor;

namespace Iverson.Api.Consumers;

/// <summary>
/// One method both <see cref="PopularitySignalConsumer"/> and Task 5's reconciliation sweep call:
/// recompute a single configured relation's child count for one parent and write it onto that
/// parent's Qdrant point payload.
///
/// Two documented degrade cases, neither of which is an error worth retrying immediately — a
/// later event or the reconciliation sweep will correct a skipped update:
///   * <see cref="IEngagementStoreSearchService.AggregateAsync"/> returns null — the tenant-scoped
///     branch's own graceful outcome for an unprovisioned tenant/table, exactly what a
///     cross-consumer startup race can produce (CDR round 2, finding 2.1).
///   * <see cref="IVectorWriteService.SetPayloadAsync"/> throws Qdrant NotFound — the parent's
///     point doesn't exist yet (spec §2).
/// </summary>
internal sealed class PopularitySignalUpdater(
    IEngagementStoreSearchService search,
    IVectorWriteService vector,
    IntelligenceTenantScope tenantScope,
    ILogger<PopularitySignalUpdater> logger)
{
    internal async Task UpdateAsync(
        SchemaDescriptor parentSchema, PopularitySignalEntry signal, SchemaDescriptor childSchema,
        SchemaRelationDescriptor relation, string parentKey, string? tenantId)
    {
        var query = new SearchQuery
        {
            Clauses =
            {
                new SearchClause
                {
                    Property   = relation.ForeignKey, // raw column-name casing — see plan assumption #17
                    Operator   = SearchOperator.Equals,
                    ClauseType = SearchClauseType.Filter,
                    Value      = new SearchValue { StringVal = parentKey }
                }
            }
        };
        var spec = new AggregationDescriptor("count", AggregationKind.Count, Field: "");
        var authzConstraints = new Dictionary<string, AuthorizationConstraint>(StringComparer.OrdinalIgnoreCase)
        {
            [childSchema.TypeName] = new AuthorizationConstraint(
                AllowedFields: null, OwnerColumn: null, OwnerValue: null,
                TenantColumn: childSchema.TenantColumn, TenantValue: tenantId)
        };

        EngagementAggResult? result;
        try
        {
            result = await search.AggregateAsync(
                SchemaBuilder.ToEngagementQuerySchema(childSchema),
                query,
                spec,
                authz: authzConstraints);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "[PopularitySignal] AggregateAsync failed for parent={Parent} type={Type} relation={Relation}; skipping.",
                parentKey.SanitizeForLog(), parentSchema.TypeName.SanitizeForLog(), signal.Relation.SanitizeForLog());
            return;
        }

        if (result is null)
        {
            // AggregateAsync's tenant-scoped branch's own graceful outcome for an unprovisioned
            // tenant/table — exactly what the cross-consumer race can produce. Skip; a later event
            // or the reconciliation sweep will retry. See CDR round 2, finding 2.1.
            logger.LogInformation(
                "[PopularitySignal] AggregateAsync returned null for parent={Parent} type={Type}; skipping this update.",
                parentKey.SanitizeForLog(), parentSchema.TypeName.SanitizeForLog());
            return;
        }

        var count      = (long)(result.MetricValue ?? 0.0);
        var collection = tenantScope.ResolveCollectionName(parentSchema.CollectionName!, tenantId, isChunks: false);
        var pointId    = IntelligenceStoreConsumer.KeyToUlong(parentKey);
        var fieldName  = signal.Relation.ToCamelCase() + "Count";

        IReadOnlyList<SrAggBucket>? buckets = null;
        if (childSchema.PopularitySignalColumn is { } tsColumn)
        {
            try
            {
                var histSpec = new AggregationDescriptor(
                    "buckets", AggregationKind.DateHistogram, tsColumn, CalendarInterval: "month");
                var hist = await search.AggregateAsync(
                    SchemaBuilder.ToEngagementQuerySchema(childSchema), query, histSpec, authz: authzConstraints);
                buckets = hist?.Buckets;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "[PopularitySignal] histogram failed for parent={Parent} type={Type}; writing an empty series.",
                    parentKey.SanitizeForLog(), parentSchema.TypeName.SanitizeForLog());
            }
        }

        var series = buckets is null
            ? ""
            : string.Join(";", buckets.TakeLast(60).Select(b => $"{b.Key}:{b.DocCount}"));

        try
        {
            using (RequestHeaders.Use("api-key", tenantScope.MintScopedApiKey(collection, readOnly: false)))
                await vector.SetPayloadAsync(collection, pointId, new Dictionary<string, object>
                {
                    [fieldName]             = count,
                    [fieldName + "Buckets"] = series
                });
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            // The parent's Qdrant point doesn't exist yet — degrade, don't retry (spec §2).
            logger.LogInformation(
                "[PopularitySignal] SetPayloadAsync NotFound for parent={Parent} collection={Collection}; skipping.",
                parentKey.SanitizeForLog(), collection.SanitizeForLog());
        }
    }
}

/// <summary>
/// Watches the entity-event stream for Created/Updated/Deleted events on the CHILD side of a
/// configured <see cref="PopularitySignalOptions.Signals"/> entry, and recomputes/writes the
/// parent's popularity count via <see cref="PopularitySignalUpdater"/>.
///
/// Structured on <see cref="DocumentRerenderConsumer"/>'s exact pattern for OneToMany FK
/// extraction (current + prior payload, old/new parent on reassignment) and its Created/Updated-
/// vs-Deleted tenant resolution split — but keyed off the configured signal list (via
/// <see cref="SchemaDescriptor.Relations"/>) instead of <see cref="SchemaRegistry.GetDependents"/>,
/// since only explicitly configured relations feed a popularity count.
///
/// Internal (not public): it depends on the internal <see cref="PopularitySignalUpdater"/> as a
/// constructor parameter, so it cannot itself be public (CS0051) — the same internal-worker
/// convention already used by <c>DlqMonitorConsumer</c> and <c>ReconciliationQueueWorker</c> in
/// this assembly. <c>AddHostedService&lt;T&gt;</c> and the test assembly's
/// <c>InternalsVisibleTo</c> both work fine with an internal type.
/// </summary>
internal sealed class PopularitySignalConsumer(
    IEventConsumer consumer,
    SchemaRegistry registry,
    IEntityRepository entities,
    IOptions<PopularitySignalOptions> options,
    PopularitySignalUpdater updater,
    ILogger<PopularitySignalConsumer> logger) : BackgroundService
{
    private const string GroupId = "iverson.consumer.popularity-signal";

    protected override Task ExecuteAsync(CancellationToken ct) =>
        ConsumerResilience.RunWithRestartAsync(
            () => consumer.ConsumeAsync(EntityTopics.Events, GroupId, DispatchAsync, ct),
            logger, "PopularitySignal", ct);

    internal async Task DispatchAsync(string key, string value, CancellationToken ct)
    {
        var ev = Deserialize(key, value);

        // Which configured signal(s) does this event's type feed, as the CHILD side?
        var matches = options.Value.Signals
            .Select(s => (Signal: s, ParentSchema: registry.Get(s.ParentType)))
            .Where(m => m.ParentSchema is not null)
            .Select(m => (m.Signal, ParentSchema: m.ParentSchema!,
                Relation: m.ParentSchema!.Relations.FirstOrDefault(r =>
                    string.Equals(r.PropertyName, m.Signal.Relation, StringComparison.OrdinalIgnoreCase))))
            .Where(m => m.Relation is not null &&
                string.Equals(m.Relation!.RelatedTypeName, ev.TypeName, StringComparison.OrdinalIgnoreCase))
            .Select(m => (m.Signal, m.ParentSchema, Relation: m.Relation!)) // null-forgive once, post-filter, so `relation` below is non-nullable
            .ToList();

        if (matches.Count == 0) return;

        var childSchema = registry.Get(ev.TypeName);
        if (childSchema is null) return;

        var tenantId = await ResolveTenantIdAsync(ev, childSchema, ct);
        if (tenantId is null)
        {
            // mirrors DocumentRerenderConsumer.cs:68 — an unresolvable tenant must never reach a
            // StarRocks-bound call downstream
            logger.LogWarning(
                "[PopularitySignal] Dropped event — no authoritative row for type={Type} key={Key}",
                ev.TypeName.SanitizeForLog(), ev.Key.SanitizeForLog());
            return;
        }

        JsonElement payload;
        try
        {
            using var payloadDoc = JsonDocument.Parse(ev.PayloadJson);
            payload = payloadDoc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new PoisonMessageException($"[PopularitySignal] Malformed payload JSON key={ev.Key}", ex);
        }

        JsonElement? priorPayload = null;
        if (ev.PriorPayloadJson is not null)
        {
            try
            {
                using var priorDoc = JsonDocument.Parse(ev.PriorPayloadJson);
                priorPayload = priorDoc.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                throw new PoisonMessageException($"[PopularitySignal] Malformed payload JSON key={ev.Key}", ex);
            }
        }

        foreach (var (signal, parentSchema, relation) in matches)
        {
            try
            {
                var newParentKey = ExtractString(payload, relation.ForeignKey);
                if (newParentKey is not null)
                    await updater.UpdateAsync(parentSchema, signal, childSchema, relation, newParentKey, tenantId);

                if (priorPayload is not null)
                {
                    var oldParentKey = ExtractString(priorPayload.Value, relation.ForeignKey);
                    if (oldParentKey is not null && oldParentKey != newParentKey)
                        await updater.UpdateAsync(parentSchema, signal, childSchema, relation, oldParentKey, tenantId);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "[PopularitySignal] Failed to update parent for signal={Signal} type={Type} key={Key} — skipping.",
                    signal.Relation.SanitizeForLog(), ev.TypeName.SanitizeForLog(), ev.Key.SanitizeForLog());
            }
        }
    }

    // Created/Updated re-derive from the authoritative Postgres row (the event payload is
    // unsigned and must not decide which rows a tenant-scoped lookup returns); Deleted reads
    // the pre-delete snapshot. See ProjectionTenantResolution for the drop-vs-dead-letter rule.
    private async Task<string?> ResolveTenantIdAsync(EntityEvent ev, SchemaDescriptor changedSchema, CancellationToken ct) =>
        ev.EventType == EntityEventType.Deleted
            ? ProjectionTenantResolution.TenantFromSnapshot(ev.PayloadJson, changedSchema, ev.Key, "[PopularitySignal]")
            : (await ProjectionTenantResolution.FetchAuthoritativeRowAsync(entities, changedSchema, ev.Key, "[PopularitySignal]"))?.TenantId;

    // Identical shape to DocumentRerenderConsumer.cs:195.
    private static EntityEvent Deserialize(string key, string value)
    {
        EntityEvent? ev;
        try
        {
            ev = JsonSerializer.Deserialize<EntityEvent>(value, s_jsonOptions);
        }
        catch (JsonException ex)
        {
            throw new PoisonMessageException($"[PopularitySignal] Malformed event JSON key={key}", ex);
        }

        return ev ?? throw new PoisonMessageException($"[PopularitySignal] Event deserialized to null key={key}");
    }

    // A deliberate additional copy of the pattern in DocumentRerenderConsumer.cs (and
    // DocumentRenderer.cs, EnrichmentConsumer.cs, IntelligenceStoreConsumer.cs) — the spec
    // authorizes adding a copy here rather than extracting a shared helper and touching any of
    // those files.
    private static string? ExtractString(JsonElement payload, string propertyName)
    {
        if (payload.TryGetProperty(propertyName, out var v))
            return v.ValueKind == JsonValueKind.String ? v.GetString()
                 : v.ValueKind == JsonValueKind.Null   ? null
                 : v.ToString();

        var camel = char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
        if (payload.TryGetProperty(camel, out var vc))
            return vc.ValueKind == JsonValueKind.String ? vc.GetString()
                 : vc.ValueKind == JsonValueKind.Null   ? null
                 : vc.ToString();

        return null;
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}
