using System.Text.Json;
using Iverson.Api.Schema;
using Iverson.Events;
using Iverson.Sql;

namespace Iverson.Api.Consumers;

/// <summary>
/// Watches the entity-event stream and decides whose rendered documents (T1/T4) went stale
/// because a related entity changed, enqueueing per-entity re-render rows onto T6's
/// <see cref="IDocumentRerenderQueueRepository"/>. T8 drains that queue and does the actual
/// re-render; this consumer only detects and enqueues.
///
/// <see cref="EntityEvent.SuppressRerenderCascade"/> is checked first, before any other work:
/// T8 republishes an entity event to trigger a re-render, and if this consumer acted on that
/// republished event it would enqueue another re-render for the same entity, forever. This is
/// the only thing standing between this feature and an infinite loop.
/// </summary>
public sealed class DocumentRerenderConsumer(
    IEventConsumer consumer,
    SchemaRegistry registry,
    IEntityRepository entities,
    IDocumentRerenderQueueRepository queue,
    ILogger<DocumentRerenderConsumer> logger) : BackgroundService
{
    private const string GroupId = "iverson.consumer.document-rerender";

    protected override Task ExecuteAsync(CancellationToken ct) =>
        ConsumerResilience.RunWithRestartAsync(
            () => consumer.ConsumeAsync(EntityTopics.Events, GroupId, DispatchAsync, ct),
            logger,
            "DocumentRerender",
            ct);

    internal async Task DispatchAsync(string key, string value, CancellationToken ct)
    {
        var ev = Deserialize(key, value);

        // Loop breaker — see class doc. Must be the very first check.
        if (ev.SuppressRerenderCascade) return;

        var dependents = registry.GetDependents(ev.TypeName);
        if (dependents.Count == 0) return;

        var changedSchema = registry.Get(ev.TypeName);
        if (changedSchema is null) return;

        // Tenant sourcing deliberately splits by event type — the same split
        // IntelligenceStoreConsumer makes between HandleAsync and HandleDeleteAsync. A null
        // tenant is not an error (RunAsRoleAsync sets the RLS GUC to NULL and any scoped
        // lookup below would silently return zero rows), but the OneToMany branch below reads
        // its parent key straight out of the payload with no query in between, bypassing that
        // natural zero-rows gate. Returning early here is what keeps a null tenant from ever
        // reaching EnqueueEntityAsync — see T6 review note: the partial unique index on
        // ("TenantId","TypeName","EntityKey") does not collapse duplicate NULL-tenant rows.
        //
        // A null here means only one thing: the authoritative Postgres row is already gone by
        // consumption time (ProjectionTenantResolution.FetchAuthoritativeRowAsync returned null),
        // and the entity's Deleted event follows. A row or delete snapshot that is present but
        // carries no tenant value throws PoisonMessageException inside the helper instead.
        var tenantId = await ResolveTenantIdAsync(ev, changedSchema, ct);
        if (tenantId is null)
        {
            logger.LogWarning(
                "[DocumentRerender] Dropped event — no authoritative row for type={Type} key={Key}",
                ev.TypeName.SanitizeForLog(), ev.Key.SanitizeForLog());
            return;
        }

        using var payloadDoc = JsonDocument.Parse(ev.PayloadJson);
        var payload = payloadDoc.RootElement;

        JsonElement? priorPayload = null;
        if (ev.PriorPayloadJson is not null)
        {
            using var priorDoc = JsonDocument.Parse(ev.PriorPayloadJson);
            priorPayload = priorDoc.RootElement.Clone();
        }

        foreach (var (declaringTypeName, relation) in dependents)
        {
            // This consumer has its own Kafka group, so an unhandled throw here would stall its
            // offset commit for the WHOLE topic — one persistently failing dependent (a dropped
            // schema, a malformed FK column) would starve re-render detection for every type on
            // the topic, not just this one. Isolate per-dependent and keep going.
            try
            {
                var declaringSchema = registry.Get(declaringTypeName);
                if (declaringSchema is null) continue;

                switch (relation.Kind)
                {
                    case RelationKind.ManyToOne:
                    case RelationKind.OneToOne:
                        // The FK lives on the DECLARING row, pointing at the changed entity — find
                        // declaring rows whose FK column equals the changed entity's key.
                        await EnqueueByColumnAsync(declaringSchema, relation.ForeignKey, ev.Key, tenantId);
                        break;

                    case RelationKind.OneToMany:
                        // The FK lives on the CHANGED row (the child), pointing back at its parent
                        // (the declaring entity) — read it straight out of the payload. FK
                        // reassignment (the parent value moved) must enqueue BOTH the old and the
                        // new parent: the new parent comes from the current payload, the old parent
                        // only from PriorPayloadJson (null on Created).
                        await EnqueueOneToManyParentsAsync(declaringSchema, relation, payload, priorPayload, tenantId);
                        break;

                    case RelationKind.ManyToMany:
                        // The FK is a uuid[] on the DECLARING row — find declaring rows whose array
                        // contains the changed entity's key.
                        await EnqueueByArrayContainsAsync(declaringSchema, relation.ForeignKey, ev.Key, tenantId);
                        break;

                    default:
                        throw new ArgumentOutOfRangeException(nameof(relation.Kind), relation.Kind,
                            $"Unhandled {nameof(RelationKind)} value — add a case above.");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "[DocumentRerender] Failed to enqueue re-render for dependent type={DeclaringType} " +
                    "relation={Relation} of changed type={ChangedType} key={Key} — skipping this dependent.",
                    declaringTypeName.SanitizeForLog(), relation.PropertyName.SanitizeForLog(),
                    ev.TypeName.SanitizeForLog(), ev.Key.SanitizeForLog());
            }
        }
    }

    // Created/Updated re-derive from the authoritative Postgres row (the event payload is
    // unsigned and must not decide which rows a tenant-scoped lookup returns); Deleted reads
    // the pre-delete snapshot. See ProjectionTenantResolution for the drop-vs-dead-letter rule.
    private async Task<string?> ResolveTenantIdAsync(EntityEvent ev, SchemaDescriptor changedSchema, CancellationToken ct) =>
        ev.EventType == EntityEventType.Deleted
            ? ProjectionTenantResolution.TenantFromSnapshot(ev.PayloadJson, changedSchema, ev.Key, "[DocumentRerender]")
            : (await ProjectionTenantResolution.FetchAuthoritativeRowAsync(entities, changedSchema, ev.Key, "[DocumentRerender]"))?.TenantId;

    private async Task EnqueueByColumnAsync(SchemaDescriptor declaringSchema, string foreignKey, string changedKey, string tenantId)
    {
        var rows = await entities.FetchByColumnAsync(
            SchemaBuilder.ToTableSchema(declaringSchema), foreignKey, changedKey, EntityAccess.ForTenant(tenantId));

        foreach (var rowJson in rows)
        {
            using var doc = JsonDocument.Parse(rowJson);
            var declaringKey = ExtractString(doc.RootElement, declaringSchema.KeyColumn.Name);
            if (declaringKey is not null)
                await queue.EnqueueEntityAsync(tenantId, declaringSchema.TypeName, declaringKey);
        }
    }

    private async Task EnqueueByArrayContainsAsync(SchemaDescriptor declaringSchema, string foreignKey, string changedKey, string tenantId)
    {
        var rows = await entities.FetchByArrayContainsAsync(
            SchemaBuilder.ToTableSchema(declaringSchema), foreignKey, changedKey, EntityAccess.ForTenant(tenantId));

        foreach (var rowJson in rows)
        {
            using var doc = JsonDocument.Parse(rowJson);
            var declaringKey = ExtractString(doc.RootElement, declaringSchema.KeyColumn.Name);
            if (declaringKey is not null)
                await queue.EnqueueEntityAsync(tenantId, declaringSchema.TypeName, declaringKey);
        }
    }

    private async Task EnqueueOneToManyParentsAsync(
        SchemaDescriptor declaringSchema, RelationDescriptor relation,
        JsonElement payload, JsonElement? priorPayload, string tenantId)
    {
        var newParentKey = ExtractString(payload, relation.ForeignKey);
        if (newParentKey is not null &&
            await entities.FetchByKeyAsync(
                SchemaBuilder.ToTableSchema(declaringSchema), newParentKey, EntityAccess.ForTenant(tenantId)) is not null)
        {
            await queue.EnqueueEntityAsync(tenantId, declaringSchema.TypeName, newParentKey);
        }

        if (priorPayload is not null)
        {
            var oldParentKey = ExtractString(priorPayload.Value, relation.ForeignKey);
            if (oldParentKey is not null && oldParentKey != newParentKey &&
                await entities.FetchByKeyAsync(
                    SchemaBuilder.ToTableSchema(declaringSchema), oldParentKey, EntityAccess.ForTenant(tenantId)) is not null)
            {
                await queue.EnqueueEntityAsync(tenantId, declaringSchema.TypeName, oldParentKey);
            }
        }
    }

    private static EntityEvent Deserialize(string key, string value)
    {
        EntityEvent? ev;
        try
        {
            ev = JsonSerializer.Deserialize<EntityEvent>(value, s_jsonOptions);
        }
        catch (JsonException ex)
        {
            throw new PoisonMessageException($"[DocumentRerender] Malformed event JSON key={key}", ex);
        }

        return ev ?? throw new PoisonMessageException($"[DocumentRerender] Event deserialized to null key={key}");
    }

    // A deliberate additional copy of the pattern in DocumentRenderer.cs, EnrichmentConsumer.cs
    // and IntelligenceStoreConsumer.cs — the spec authorizes adding a copy here rather than
    // extracting a shared helper and touching any of those files.
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
