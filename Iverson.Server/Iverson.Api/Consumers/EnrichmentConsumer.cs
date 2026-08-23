using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Embeddings;
using Iverson.Events;
using Iverson.Sql;

namespace Iverson.Api.Consumers;

/// <summary>
/// Subscribes to entity.created / entity.updated / entity.deleted and fills the type's
/// enrichment target columns ([IversonSummary] / [IversonKeywords] / [IversonExtracted])
/// from an Ollama generative model.
///
/// Loop prevention: the consumer hashes the object's source text (the concatenation of the
/// type's [IversonEmbedding]/[IversonChunk] property values) *plus* the type's enrichment
/// specification (the ordered set of target column, kind and hint), and skips the object when
/// that hash matches the one recorded in iverson_enrichment_state. A writeback only mutates
/// enrichment target columns — never the source text and never the specification — so the
/// entity.updated it republishes hashes identically and is dropped on the second pass.
/// Including the specification in the hash is what makes a newly declared target or an edited
/// [IversonExtracted] hint re-enrich existing objects (and what lets ReconcileTypeAsync serve
/// as the operational trigger for that).
///
/// Enrichment is best-effort: it must never block or fail an object's projection into the
/// stores, so a generation or writeback failure logs and returns rather than throwing
/// PoisonMessageException.
/// </summary>
public sealed class EnrichmentConsumer(
    IEventConsumer consumer,
    SchemaRegistry registry,
    IEntityRepository entities,
    IEnrichmentStateRepository state,
    IOutboxWriter outboxWriter,
    IOutboxPublisher outboxPublisher,
    IRecordStoreTransactionRunner txRunner,
    IEnrichmentService enrichment,
    ILogger<EnrichmentConsumer> logger) : BackgroundService
{
    private const string GroupId = "iverson.consumer.enrichment";

    protected override Task ExecuteAsync(CancellationToken ct) =>
        ConsumerResilience.RunWithRestartAsync(
            () => consumer.ConsumeAsync(EntityTopics.Events, GroupId, DispatchAsync, ct),
            logger,
            "Enrichment",
            ct);

    internal async Task DispatchAsync(string key, string value, CancellationToken ct)
    {
        var ev = Deserialize(key, value);
        switch (ev.EventType)
        {
            case EntityEventType.Created:
            case EntityEventType.Updated:
                await HandleAsync(key, value, ct);
                break;
            case EntityEventType.Deleted:
                await HandleDeleteAsync(key, value, ct);
                break;
        }
    }

    internal async Task HandleAsync(string key, string value, CancellationToken ct)
    {
        var ev = Deserialize(key, value);

        var schema = registry.Get(ev.TypeName);
        if (schema is null)
        {
            logger.LogError(
                "[Enrichment] Dropped event — no schema registered for type={Type} key={Key}.",
                ev.TypeName.SanitizeForLog(), key);
            Activity.Current?
                .SetTag("dropped_event", true)
                .SetTag("dropped_event.reason", "schema_not_found")
                .SetTag("dropped_event.type", ev.TypeName);
            return;
        }

        if (schema.EnrichmentTargets.Count == 0) return;

        // ── Step 1: fetch the authoritative row and re-derive the tenant ──────────
        // The tenant value has to be derived here, before the hash lookup, because the
        // state row's key includes tenant_id: a lookup without it could never match the
        // row the writeback later writes under the real tenant, so every event would
        // re-enrich and every writeback would republish entity.updated — the loop breaker
        // inverted into unbounded re-enrichment.
        var tableSchema = SchemaBuilder.ToTableSchema(schema);
        var rowJson = await entities.FetchByKeyAsync(tableSchema, ev.Key);
        if (rowJson is null)
        {
            logger.LogWarning(
                "[Enrichment] No authoritative row for type={Type} key={Key} — skipping.",
                ev.TypeName.SanitizeForLog(), key);
            return;
        }

        JsonElement row;
        try
        {
            using var doc = JsonDocument.Parse(rowJson);
            row = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            logger.LogError(ex,
                "[Enrichment] Malformed authoritative row JSON for type={Type} key={Key} — skipping.",
                ev.TypeName.SanitizeForLog(), key);
            return;
        }

        // Fail closed when the authoritative row carries no tenant value, and deliberately write
        // NO state row: with a null tenant EnterTenantScopeAsync sets app.tenant_id to NULL, the
        // RLS predicate fails closed and the targeted UPDATE would match zero rows — recording a
        // hash anyway would mark the object enriched forever while it carried none of the
        // enriched values.
        var tenantValue = ExtractString(row, schema.TenantColumn);
        if (tenantValue is null)
        {
            logger.LogWarning(
                "[Enrichment] Skipped — no authoritative tenant value for type={Type} key={Key}; no state row written.",
                ev.TypeName.SanitizeForLog(), key);
            return;
        }

        // ── Step 2: hash source text + enrichment specification, and compare ──────
        var sourceText = BuildSourceText(schema, row);
        var hash = ComputeHash(sourceText, schema.EnrichmentTargets);

        var storedHash = await state.GetHashAsync(tenantValue, schema.TypeName, ev.Key);
        if (string.Equals(storedHash, hash, StringComparison.Ordinal))
        {
            logger.LogDebug(
                "[Enrichment] Skipped {Type}:{Key} — source text and specification unchanged.",
                schema.TypeName.SanitizeForLog(), ev.Key);
            return;
        }

        // ── Steps 3-5: generate, write back, publish ──────────────────────────────
        // Best-effort by contract: enrichment must never block or fail an object's
        // projection into the stores, so nothing below throws PoisonMessageException.
        try
        {
            var columns = await GenerateAsync(schema, sourceText, ct);
            if (columns.Count == 0)
            {
                logger.LogWarning(
                    "[Enrichment] Generated no values for {Type}:{Key} — no writeback, no state row.",
                    schema.TypeName.SanitizeForLog(), ev.Key);
                return;
            }

            var outboxRowId = Guid.CreateVersion7();

            await txRunner.ExecuteInTransactionAsync(async tx =>
            {
                // SET LOCAL ROLE iverson_runtime persists for the remainder of the
                // transaction, and neither the enrichment-state table nor the outbox has a
                // grant for that role — so tenant scope must be exited before either write.
                // OutboxWriter.UpsertAndEnqueueOutboxAsync performs the identical sequence.
                await tx.EnterTenantScopeAsync(tenantValue);
                await entities.UpdateColumnsAsync(tx, tableSchema, ev.Key, columns);
                await tx.ExitTenantScopeAsync();

                await state.UpsertAsync(
                    tx, tenantValue, schema.TypeName, ev.Key, hash, DateTimeOffset.UtcNow);
                await outboxWriter.EnqueueUpdateOutboxRowAsync(
                    tx, outboxRowId, schema.TypeName, ev.Key, rowJson);
            });

            // Re-fetch after commit rather than publishing the pre-generation snapshot with the
            // enriched columns merged in: that snapshot predates any client update that landed
            // during the LLM call, so publishing it would carry stale column values to StarRocks
            // and Qdrant and win over the client's own event — reintroducing on the publish path
            // exactly the clobber the targeted UPDATE removes from the write path.
            // ReconciliationService.ProcessOneAsync re-fetches before republishing for the same reason.
            var publishJson = await entities.FetchByKeyAsync(tableSchema, ev.Key);
            if (publishJson is null)
            {
                logger.LogWarning(
                    "[Enrichment] Row vanished before republish for {Type}:{Key} — outbox row remains for the reconciliation worker.",
                    schema.TypeName.SanitizeForLog(), ev.Key);
                return;
            }

            await outboxPublisher.PublishAsync(
                EntityEventType.Updated,
                schema.TypeName,
                ev.Key,
                publishJson,
                requestTraceId: null,
                StoreTargeting.DetermineTargetStores(schema),
                outboxRowId,
                "Enrichment",
                ct);

            logger.LogInformation(
                "[Enrichment] Enriched {Count} column(s) for {Type}:{Key}",
                columns.Count, schema.TypeName.SanitizeForLog(), ev.Key);
        }
        catch (Exception ex)
        {
            // Leaves no state row, so the next event for this object retries.
            logger.LogError(ex,
                "[Enrichment] Failed for {Type}:{Key} — object left intact and unenriched.",
                schema.TypeName.SanitizeForLog(), ev.Key);
        }
    }

    internal async Task HandleDeleteAsync(string key, string value, CancellationToken ct)
    {
        var ev = Deserialize(key, value);

        var schema = registry.Get(ev.TypeName);
        if (schema is null || schema.EnrichmentTargets.Count == 0) return;

        // The Postgres row is already gone by the time a delete event is consumed
        // (IntelligenceStoreConsumer.cs:248-250), so unlike HandleAsync the tenant value must
        // come from the pre-delete snapshot ObjectMappingGrpcService.Delete published in
        // ev.PayloadJson. Leaving the state row behind is not safe: a client-supplied key
        // (ObjectMappingGrpcService.cs:127-132) lets a delete-then-recreate of the same key
        // hash equal against the orphan row and be skipped forever.
        JsonElement payload;
        try
        {
            using var doc = JsonDocument.Parse(ev.PayloadJson);
            payload = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            logger.LogError(ex,
                "[Enrichment] Malformed delete payload JSON for type={Type} key={Key} — state row not removed.",
                ev.TypeName.SanitizeForLog(), key);
            return;
        }

        var tenantValue = ExtractString(payload, schema.TenantColumn);
        if (tenantValue is null)
        {
            logger.LogWarning(
                "[Enrichment] Dropped delete — no tenant value in payload for type={Type} key={Key}",
                ev.TypeName.SanitizeForLog(), key);
            return;
        }

        await state.DeleteAsync(tenantValue, schema.TypeName, ev.Key);
        logger.LogInformation(
            "[Enrichment] Removed enrichment state for {Type}:{Key}",
            schema.TypeName.SanitizeForLog(), ev.Key);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<Dictionary<string, object?>> GenerateAsync(
        SchemaDescriptor schema, string sourceText, CancellationToken ct)
    {
        var columns = new Dictionary<string, object?>(schema.EnrichmentTargets.Count);

        foreach (var target in schema.EnrichmentTargets)
        {
            var generated = target.Kind switch
            {
                EnrichmentKind.Summary  => await enrichment.GenerateAsync(
                    string.Format(EnrichmentPrompts.Summary, sourceText), ct),
                EnrichmentKind.Keywords => await enrichment.GenerateAsync(
                    string.Format(EnrichmentPrompts.Keywords, sourceText), ct),
                // EnrichmentPrompts.Extraction carries a single {0} slot for the source text;
                // the per-target hint (mandatory for [IversonExtracted], enforced at
                // registration) is appended so the model knows what to pull out.
                EnrichmentKind.Extracted => await enrichment.GenerateJsonAsync(
                    string.Format(EnrichmentPrompts.Extraction, sourceText) +
                    $"\n\nExtract specifically: {target.Hint}", ct),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(schema), target.Kind,
                    $"Unhandled {nameof(EnrichmentKind)} value — add a case above.")
            };

            if (!string.IsNullOrWhiteSpace(generated))
                columns[target.ColumnName] = generated.Trim();
        }

        return columns;
    }

    // The source text is the concatenation of the type's [IversonEmbedding] and [IversonChunk]
    // property values, read out of the authoritative row — nothing else counts
    // (SchemaRegistrationOrchestrator.ValidateEnrichmentTargets enforces that a type declaring
    // enrichment targets has at least one such property).
    private static string BuildSourceText(SchemaDescriptor schema, JsonElement row)
    {
        var parts = new List<string>();
        foreach (var propertyName in schema.VectorFields.Select(v => v.PropertyName)
                     .Concat(schema.ChunkFields.Select(c => c.PropertyName)))
        {
            var text = ExtractString(row, propertyName);
            if (!string.IsNullOrWhiteSpace(text)) parts.Add(text);
        }

        return string.Join("\n\n", parts);
    }

    // Hashes the source text together with the type's enrichment specification. Hashing the
    // source text alone would leave a newly declared target or an edited hint permanently
    // unenriched on existing objects, and ReconcileTypeAsync could not force it either.
    private static string ComputeHash(
        string sourceText, IReadOnlyList<EnrichmentTarget> targets)
    {
        const char Sep = '\u001f';
        var sb = new StringBuilder(sourceText);
        sb.Append(Sep);
        foreach (var t in targets)
        {
            sb.Append(t.ColumnName).Append(Sep)
              .Append(t.Kind).Append(Sep)
              .Append(t.Hint ?? string.Empty).Append(Sep);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

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

    private static EntityEvent Deserialize(string key, string value)
    {
        EntityEvent? ev;
        try
        {
            ev = JsonSerializer.Deserialize<EntityEvent>(value, s_jsonOptions);
        }
        catch (JsonException ex)
        {
            throw new PoisonMessageException($"[Enrichment] Malformed event JSON key={key}", ex);
        }

        return ev ?? throw new PoisonMessageException($"[Enrichment] Event deserialized to null key={key}");
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}

/// <summary>
/// The one place the Enrichment__Enabled gate is expressed. <see cref="IEnrichmentService"/> is
/// registered unconditionally — other consumers resolve it (and
/// <c>IOptions&lt;EnrichmentServiceOptions&gt;</c>) regardless of the flag — and only the hosted
/// <see cref="EnrichmentConsumer"/> is gated.
/// </summary>
internal static class EnrichmentRegistration
{
    internal static IServiceCollection AddEnrichmentPipeline(
        this IServiceCollection services, IConfiguration config, bool isWorker)
    {
        services.AddEnrichment(config);

        if (isWorker && config.GetValue($"{EnrichmentServiceOptions.Section}:Enabled", true))
            services.AddHostedService<EnrichmentConsumer>();

        return services;
    }
}
