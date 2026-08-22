using Iverson.Api.Consumers;
using Iverson.Api.Schema;
using Iverson.Events;
using Iverson.Sql;
using Microsoft.Extensions.Options;

namespace Iverson.Api.Reconciliation;

/// <summary>
/// Drains T6's <see cref="IDocumentRerenderQueueRepository"/>: for a per-entity row (EntityKey
/// non-null), re-fetches the row from Postgres — the authoritative source of truth, not the
/// possibly-stale enqueue-time state — and republishes it so the fan-out pipeline re-renders the
/// document. For a type-level row (EntityKey null, a backfill/expansion registered when the key
/// set wasn't yet known), pages through the type's keys and expands each into a per-entity row.
///
/// Every republished event carries <see cref="EntityEvent.SuppressRerenderCascade"/> = true and
/// <see cref="StoreTarget.Intelligence"/> only: this is a document re-render, not a full
/// re-projection, so it must not re-run the Postgres/StarRocks projections, and it must not be
/// picked back up by <see cref="DocumentRerenderConsumer"/> as a fresh dependency change — that
/// would be an infinite loop.
///
/// Mirrors <see cref="ReconciliationQueueWorker"/>'s poll-loop shape and
/// <see cref="ReconciliationService"/>'s re-fetch-before-publish and exhaustion-warning patterns.
/// </summary>
internal sealed class DocumentRerenderQueueWorker(
    SchemaRegistry registry,
    IEntityRepository entities,
    IDocumentRerenderQueueRepository queue,
    IEventProducer events,
    IOptions<DocumentRerenderOptions> options,
    ILogger<DocumentRerenderQueueWorker> logger) : BackgroundService
{
    private DocumentRerenderOptions Options => options.Value;

    protected override Task ExecuteAsync(CancellationToken ct) =>
        ConsumerResilience.RunWithRestartAsync(
            () =>
                PollLoopAsync(ct),
                logger,
                "DocumentRerenderQueue",
                ct);

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await TickAsync(ct);
            ReconciliationTelemetry.DocumentRerenderQueueDepth = await queue.CountPendingAsync();
            await Task.Delay(Options.PollInterval, ct);
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        var opts = Options;

        // Before draining — see class doc / ReconciliationService.cs:64-71. Without this, an
        // exhausted row is invisible: it no longer retries, is never deleted, and keeps
        // inflating the queue-depth gauge, so a stalled re-render reads as ordinary backlog.
        var exhaustedCount = await queue.CountExhaustedAsync(opts.MaxAttempts);
        if (exhaustedCount > 0)
            logger.LogWarning(
                "[DocumentRerender] {Count} queued entr{Suffix} exhausted MaxAttempts ({MaxAttempts}) and " +
                "require(s) manual intervention",
                exhaustedCount, exhaustedCount == 1 ? "y" : "ies", opts.MaxAttempts);

        var rows = (await queue.PollAsync(opts.MaxAttempts, opts.BatchSize)).ToList();

        foreach (var row in rows)
        {
            if (ct.IsCancellationRequested) break;

            if (row.EntityKey is null)
                await ProcessTypeLevelRowAsync(row, opts);
            else
                await ProcessEntityRowAsync(row);
        }
    }

    private async Task ProcessEntityRowAsync(DocumentRerenderQueueRow row)
    {
        var schema = registry.Get(row.TypeName);
        if (schema is null)
        {
            // No schema registered for the queued type — nothing to render; drop the stale entry
            // (mirrors ReconciliationService.ProcessOneAsync).
            await queue.DeleteRowAsync(row.Id);
            return;
        }

        var rowJson = await entities.FetchByKeyAsync(SchemaBuilder.ToTableSchema(schema), row.EntityKey!);
        if (rowJson is null)
        {
            // Entity no longer exists in Postgres — a vanished row is dropped, not resurrected.
            await queue.DeleteRowAsync(row.Id);
            return;
        }

        try
        {
            await events.ProduceAsync(
                EntityTopics.Events,
                row.EntityKey!,
                new EntityEvent(
                    EntityEventType.Updated,
                    row.TypeName,
                    row.EntityKey!,
                    rowJson,
                    string.Empty,
                    "1",
                    DateTimeOffset.UtcNow,
                    StoreTarget.Intelligence,
                    null,
                    SuppressRerenderCascade: true));

            await queue.DeleteRowAsync(row.Id);
        }
        catch (Exception ex)
        {
            await RecordFailureAsync(row, ex);
        }
    }

    private async Task ProcessTypeLevelRowAsync(DocumentRerenderQueueRow row, DocumentRerenderOptions opts)
    {
        // Same failure handling as the per-entity drain path above — see class doc: an
        // exception escaping here reaches ConsumerResilience, which restarts the whole loop
        // against a row that sorts to the head of every batch (ordered by EnqueuedAt, and a
        // type-level row is enqueued at registration), stalling every re-render behind it.
        try
        {
            var schema = registry.Get(row.TypeName);
            if (schema is null)
            {
                await queue.DeleteTypeRowAsync(row.Id, row.EnqueuedAt);
                return;
            }

            var page = (await entities.FetchKeysAndTenantsPagedAsync(
                SchemaBuilder.ToTableSchema(schema), row.Cursor, opts.PageSize)).ToList();

            foreach (var keyed in page)
                await queue.EnqueueEntityAsync(keyed.TenantId, row.TypeName, keyed.Key);

            // Both writes are guarded on the EnqueuedAt observed at poll time. A template
            // change arriving mid-expansion re-enqueues this same row Id with Cursor reset to
            // NULL and a fresh EnqueuedAt; an unguarded delete would drop that invalidation and
            // an unguarded advance would reinstate a stale cursor, leaving every entity before
            // it rendered against the old template forever. A no-op here is the correct
            // outcome — the next tick picks up the reset row and rescans from the start.
            if (page.Count < opts.PageSize)
                await queue.DeleteTypeRowAsync(row.Id, row.EnqueuedAt);
            else
                await queue.AdvanceCursorAsync(row.Id, page[^1].Key, row.EnqueuedAt);
        }
        catch (Exception ex)
        {
            await RecordFailureAsync(row, ex);
        }
    }

    private Task RecordFailureAsync(DocumentRerenderQueueRow row, Exception ex) =>
        queue.RecordFailureAsync(row.Id, row.Attempts + 1, ex.Message);
}
