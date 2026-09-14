using Iverson.Api.Consumers;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Sql;
using Microsoft.Extensions.Options;

namespace Iverson.Api.Reconciliation;

/// <summary>
/// Periodic backstop sweep for <see cref="PopularitySignalConsumer"/>: enumerates every known
/// parent for each configured <see cref="PopularitySignalOptions.Signals"/> entry (paged,
/// tenant-aware, via <see cref="IEntityRepository.FetchKeysAndTenantsPagedAsync"/>) and recomputes
/// its popularity count through the shared <see cref="PopularitySignalUpdater"/> — the same method
/// the event-driven consumer calls. This corrects any parent the consumer skipped or missed (a
/// startup race, a dropped event, an unprovisioned tenant at the time of the triggering event).
///
/// Mirrors <see cref="ReconciliationQueueWorker"/>'s poll-loop shape exactly.
/// </summary>
internal sealed class PopularitySignalReconciliationWorker(
    IOptions<PopularitySignalOptions> options,
    SchemaRegistry registry,
    IEntityRepository entities,
    PopularitySignalUpdater updater,
    ILogger<PopularitySignalReconciliationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(10);
    private const int PageSize = 500;

    protected override Task ExecuteAsync(CancellationToken ct) =>
        ConsumerResilience.RunWithRestartAsync(() => SweepLoopAsync(ct), logger, "PopularitySignalReconciliation", ct);

    private async Task SweepLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var signal in options.Value.Signals)
                await SweepSignalAsync(signal, ct);

            await Task.Delay(SweepInterval, ct);
        }
    }

    // internal (not private) so tests can drive a single signal's sweep directly rather than
    // through the infinite SweepLoopAsync — same testability rationale as
    // DocumentRerenderQueueWorker.TickAsync and PopularitySignalConsumer.DispatchAsync.
    internal async Task SweepSignalAsync(PopularitySignalEntry signal, CancellationToken ct)
    {
        var parentSchema = registry.Get(signal.ParentType);
        var relation = parentSchema?.Relations.FirstOrDefault(r =>
            string.Equals(r.PropertyName, signal.Relation, StringComparison.OrdinalIgnoreCase));
        var childSchema = relation is not null ? registry.Get(relation.RelatedTypeName) : null;
        if (parentSchema is null || relation is null || childSchema is null) return; // schema unregistered mid-run; skip this sweep

        string? afterKey = null;
        while (!ct.IsCancellationRequested)
        {
            // Cross-tenant by design: this sweep enumerates every parent of this type across every
            // tenant, per-row deriving the tenant to update through (row.TenantId below) — the same
            // reasoning as DocumentRerenderQueueWorker's identical call.
            var page = (await entities.FetchKeysAndTenantsPagedAsync(
                SchemaBuilder.ToTableSchema(parentSchema), afterKey, PageSize,
                EntityAccess.CrossTenantMaintenance)).ToList();
            if (page.Count == 0) break;

            foreach (var row in page)
            {
                if (row.TenantId is null) continue; // same principle as PopularitySignalConsumer's
                                                      // tenant guard — never reach a StarRocks-bound
                                                      // call with an unresolvable tenant
                try
                {
                    await updater.UpdateAsync(parentSchema, signal, childSchema, relation, row.Key, row.TenantId);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "[PopularitySignalReconciliation] Failed to update parent={Parent} for signal={Signal} — skipping.",
                        row.Key.SanitizeForLog(), signal.Relation.SanitizeForLog());
                }
            }

            afterKey = page[^1].Key;
        }
    }
}
