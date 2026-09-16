using System.Diagnostics;
using Iverson.Api.Schema;
using Iverson.Events;
using Iverson.Sql;

namespace Iverson.Api.Reconciliation;

/// <summary>
/// Two ways to re-project entities from Postgres (the system of record) back through the
/// fan-out pipeline: a full-table replay for manual/admin use (<see cref="ReconcileTypeAsync"/>,
/// backing the existing POST /admin/reconcile/{typeName} endpoint), and a targeted replay of
/// just the rows a fire-and-forget Kafka publish is known to have failed for
/// (<see cref="ProcessQueuedFailuresAsync"/>, driven by a background worker).
/// </summary>
internal sealed class ReconciliationService(
    SchemaRegistry registry,
    IEntityRepository entities,
    IReconciliationQueueRepository queue,
    IEventProducer events,
    ILogger<ReconciliationService> logger)
{
    public const int MaxAttempts = 10;
    private const int BatchSize = 100;

    public async Task<int?> ReconcileTypeAsync(string typeName, CancellationToken ct = default)
    {
        var schema = registry.Get(typeName);
        if (schema is null) return null;

        var tableSchema = SchemaBuilder.ToTableSchema(schema);
        var targetStores = StoreTargeting.DetermineTargetStores(schema);
        var traceId = Activity.Current?.TraceId.ToString() ?? string.Empty;
        var count = 0;
        string? afterKey = null;

        // Cross-tenant by design: an admin reconcile of a type re-projects EVERY tenant's rows
        // of that type back through the fan-out pipeline. Paginated (not FetchAllAsync) so the
        // whole table is never held in memory at once; the loop observes `ct` itself so a client
        // disconnect actually stops the work (passing the token alone stops nothing, since none
        // of FetchKeysAndTenantsPagedAsync/FetchManyByKeysAsync/ProduceAsync accepts one).
        while (!ct.IsCancellationRequested)
        {
            var page = (await entities.FetchKeysAndTenantsPagedAsync(
                tableSchema, afterKey, BatchSize, EntityAccess.CrossTenantMaintenance)).ToList();
            if (page.Count == 0) break;

            var rows = (await entities.FetchManyByKeysAsync(
                tableSchema, page.Select(p => p.Key).ToList(), EntityAccess.CrossTenantMaintenance)).ToList();

            foreach (var row in rows)
            {
                await events.ProduceAsync(
                    EntityTopics.Events,
                    row.Key,
                    new EntityEvent(
                        EntityEventType.Updated,
                        typeName,
                        row.Key,
                        row.Data,
                        traceId,
                        "1",
                        DateTimeOffset.UtcNow,
                        targetStores));
                count++;
            }

            afterKey = page[^1].Key;
        }

        logger.LogInformation("[Reconcile] Re-projected {Count} {Type} records to Kafka", count, typeName.SanitizeForLog());
        return count;
    }

    public async Task ProcessQueuedFailuresAsync(CancellationToken ct)
    {
        var rows = (await queue.PollQueuedFailuresAsync(MaxAttempts, BatchSize)).ToList();

        var exhaustedCount = await queue.CountExhaustedAsync(MaxAttempts);

        if (exhaustedCount > 0)
            logger.LogWarning(
                "[Reconciliation] {Count} queued entr{Suffix} exhausted MaxAttempts ({MaxAttempts}) and " +
                "require(s) manual reconciliation via the admin reconcile endpoint",
                exhaustedCount, exhaustedCount == 1 ? "y" : "ies", MaxAttempts);

        foreach (var row in rows)
        {
            if (ct.IsCancellationRequested) break;
            await ProcessOneAsync(row);
        }
    }

    public Task<int> CountPendingAsync() => queue.CountPendingAsync();

    private async Task ProcessOneAsync(ReconciliationQueueRow row)
    {
        if (row.EventType == "Deleted")
        {
            await ProcessDeleteRowAsync(row);
            return;
        }

        var schema = registry.Get(row.TypeName);
        if (schema is null)
        {
            logger.LogWarning(
                "[Reconciliation] No schema registered for queued type={Type} key={Key} — dropping stale entry",
                row.TypeName, row.EntityKey);
            await DeleteQueueRowAsync(row.Id);
            return;
        }

        // Cross-tenant by design: the queue row records a type and key, not a tenant — the
        // replay has to reach whichever tenant's row failed to publish.
        var rowJson = await entities.FetchByKeyAsync(
            SchemaBuilder.ToTableSchema(schema), row.EntityKey, EntityAccess.CrossTenantMaintenance);

        if (rowJson is null)
        {
            // Entity no longer exists in Postgres (e.g. deleted after the failed publish) —
            // nothing to reconcile.
            await DeleteQueueRowAsync(row.Id);
            return;
        }

        try
        {
            var targetStores = StoreTargeting.DetermineTargetStores(schema);
            await events.ProduceAsync(
                EntityTopics.Events,
                row.EntityKey,
                new EntityEvent(
                    EntityEventType.Updated,
                    row.TypeName,
                    row.EntityKey,
                    rowJson,
                    string.Empty,
                    "1",
                    DateTimeOffset.UtcNow,
                    targetStores));

            await DeleteQueueRowAsync(row.Id);
            logger.LogInformation(
                "[Reconciliation] Re-published queued entity type={Type} key={Key} after {Attempts} prior failed attempt(s)",
                row.TypeName,
                row.EntityKey,
                row.Attempts);
        }
        catch (Exception ex)
        {
            await RecordFailureAsync(row, ex);
        }
    }

    /// <summary>
    /// Replays a delete-typed row: the entity is already gone from Postgres by the time the
    /// worker polls, so this republishes the pre-delete JSON snapshot captured in
    /// <see cref="ReconciliationQueueRow.Payload"/> at enqueue time
    /// (<see cref="Iverson.Sql.IOutboxWriter.EnqueueDeleteOutboxRowAsync"/>) instead of re-fetching —
    /// same attempts/backoff/exhaustion shape as the upsert-replay path.
    /// </summary>
    private async Task ProcessDeleteRowAsync(ReconciliationQueueRow row)
    {
        try
        {
            var schema = registry.Get(row.TypeName);
            var targetStores = schema is not null
                ? StoreTargeting.DetermineTargetStores(schema)
                : StoreTarget.All;

            await events.ProduceAsync(
                EntityTopics.Events,
                row.EntityKey,
                new EntityEvent(
                    EntityEventType.Deleted,
                    row.TypeName,
                    row.EntityKey,
                    row.Payload!,
                    string.Empty,
                    "1",
                    DateTimeOffset.UtcNow,
                    targetStores));

            await DeleteQueueRowAsync(row.Id);
            logger.LogInformation(
                "[Reconciliation] Re-published queued delete type={Type} key={Key} after {Attempts} prior failed attempt(s)",
                row.TypeName,
                row.EntityKey,
                row.Attempts);
        }
        catch (Exception ex)
        {
            await RecordFailureAsync(row, ex);
        }
    }

    private async Task RecordFailureAsync(ReconciliationQueueRow row, Exception ex)
    {
        var attempts = row.Attempts + 1;
        await queue.RecordFailureAsync(row.Id, attempts, ex.Message);

        if (attempts >= MaxAttempts)
            logger.LogCritical(
                "[Reconciliation] Giving up on type={Type} key={Key} after {Attempts} attempts — " +
                "requires a manual in-cluster HTTP/2 POST (e.g. curl --http2-prior-knowledge) to " +
                "/admin/reconcile/{Type} (kubectl port-forward or exec — not reachable via the ingress). " +
                "Last error: {Error}",
                row.TypeName,
                row.EntityKey,
                attempts,
                row.TypeName,
                ex.Message);
    }

    private Task DeleteQueueRowAsync(Guid id) => queue.DeleteRowAsync(id);
}
