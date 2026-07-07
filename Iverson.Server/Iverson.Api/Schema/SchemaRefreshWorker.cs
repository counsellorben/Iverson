using Iverson.Api.Consumers;

namespace Iverson.Api.Schema;

// Periodically reloads SchemaRegistry from Postgres so every process — api and worker
// replicas alike — eventually observes a schema registered/unregistered by any other
// replica, closing the cache-coherence gap that appeared once RegisterSchema (api) and
// Kafka consumption (worker) could run in separate processes. Mirrors
// Iverson.Api.Reconciliation.ReconciliationQueueWorker's poll-loop shape.
internal sealed class SchemaRefreshWorker(
    SchemaRegistry schemaRegistry,
    ILogger<SchemaRefreshWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    protected override Task ExecuteAsync(CancellationToken ct) =>
        ConsumerResilience.RunWithRestartAsync(
            () => PollLoopAsync(ct),
            logger,
            "SchemaRefresh",
            ct);

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(PollInterval, ct);
            await schemaRegistry.LoadAsync(ct);
        }
    }
}
