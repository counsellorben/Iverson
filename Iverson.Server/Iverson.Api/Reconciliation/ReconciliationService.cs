using System.Diagnostics;
using System.Text.Json;
using Iverson.Api.Schema;
using Iverson.Events;
using Iverson.Sql;

namespace Iverson.Api.Reconciliation;

internal sealed record ReconciliationQueueRow(string Id, string TypeName, string EntityKey, int Attempts);

/// <summary>
/// Two ways to re-project entities from Postgres (the system of record) back through the
/// fan-out pipeline: a full-table replay for manual/admin use (<see cref="ReconcileTypeAsync"/>,
/// backing the existing POST /admin/reconcile/{typeName} endpoint), and a targeted replay of
/// just the rows a fire-and-forget Kafka publish is known to have failed for
/// (<see cref="ProcessQueuedFailuresAsync"/>, driven by a background worker).
/// </summary>
internal sealed class ReconciliationService(
    SchemaRegistry registry,
    IPostgresRepository db,
    IEventProducer events,
    ILogger<ReconciliationService> logger)
{
    public const int MaxAttempts = 10;
    private const int BatchSize = 100;

    public async Task<int?> ReconcileTypeAsync(string typeName, CancellationToken ct = default)
    {
        var schema = registry.Get(typeName);
        if (schema is null) return null;

        var rowJsons = await db.QueryAsync<string>(
            $"""SELECT row_to_json(t)::text FROM "{schema.TableName}" t""", null);

        var targetStores = StoreTargeting.DetermineTargetStores(schema);
        var traceId = Activity.Current?.TraceId.ToString() ?? string.Empty;
        var count = 0;

        foreach (var rowJson in rowJsons)
        {
            var key = ExtractKey(rowJson, schema.KeyColumn.Name);
            if (string.IsNullOrEmpty(key)) continue;

            await events.ProduceAsync(
                EntityTopics.Updated,
                key,
                new EntityEvent(typeName, key, rowJson, traceId, "1", DateTimeOffset.UtcNow, targetStores));
            count++;
        }

        logger.LogInformation("[Reconcile] Re-projected {Count} {Type} records to Kafka", count, typeName);
        return count;
    }

    public async Task ProcessQueuedFailuresAsync(CancellationToken ct)
    {
        var rows = (await db.QueryAsync<ReconciliationQueueRow>(
            $"""
            SELECT "Id", "TypeName", "EntityKey", "Attempts"
            FROM "{ReconciliationSchema.TableName}"
            WHERE "Attempts" < @MaxAttempts
            ORDER BY "EnqueuedAt"
            LIMIT @BatchSize
            """,
            new { MaxAttempts, BatchSize })).ToList();

        foreach (var row in rows)
        {
            if (ct.IsCancellationRequested) break;
            await ProcessOneAsync(row);
        }
    }

    private async Task ProcessOneAsync(ReconciliationQueueRow row)
    {
        var schema = registry.Get(row.TypeName);
        if (schema is null)
        {
            logger.LogWarning(
                "[Reconciliation] No schema registered for queued type={Type} key={Key} — dropping stale entry",
                row.TypeName, row.EntityKey);
            await DeleteQueueRowAsync(row.Id);
            return;
        }

        var rowJson = await db.QuerySingleOrDefaultAsync<string>(
            $"""SELECT row_to_json(t)::text FROM "{schema.TableName}" t WHERE "{schema.KeyColumn.Name}" = @Key""",
            new { Key = row.EntityKey });

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
                EntityTopics.Updated,
                row.EntityKey,
                new EntityEvent(row.TypeName, row.EntityKey, rowJson, string.Empty, "1", DateTimeOffset.UtcNow, targetStores));

            await DeleteQueueRowAsync(row.Id);
            logger.LogInformation(
                "[Reconciliation] Re-published queued entity type={Type} key={Key} after {Attempts} prior failed attempt(s)",
                row.TypeName, row.EntityKey, row.Attempts);
        }
        catch (Exception ex)
        {
            var attempts = row.Attempts + 1;
            await db.ExecuteAsync(
                $"""
                UPDATE "{ReconciliationSchema.TableName}"
                SET "Attempts" = @Attempts, "LastError" = @LastError, "LastAttemptAt" = @Now
                WHERE "Id" = @Id
                """,
                new { Attempts = attempts, LastError = ex.Message, Now = DateTimeOffset.UtcNow, row.Id });

            if (attempts >= MaxAttempts)
                logger.LogCritical(
                    "[Reconciliation] Giving up on type={Type} key={Key} after {Attempts} attempts — " +
                    "requires manual POST /admin/reconcile/{Type}. Last error: {Error}",
                    row.TypeName, row.EntityKey, attempts, row.TypeName, ex.Message);
        }
    }

    private Task DeleteQueueRowAsync(string id) =>
        db.ExecuteAsync($"""DELETE FROM "{ReconciliationSchema.TableName}" WHERE "Id" = @Id""", new { Id = id });

    private static string? ExtractKey(string rowJson, string keyColumn)
    {
        using var doc = JsonDocument.Parse(rowJson);
        if (doc.RootElement.TryGetProperty(keyColumn, out var keyEl))
            return keyEl.GetString();

        var camel = char.ToLowerInvariant(keyColumn[0]) + keyColumn[1..];
        return doc.RootElement.TryGetProperty(camel, out var camelEl) ? camelEl.GetString() : null;
    }
}
