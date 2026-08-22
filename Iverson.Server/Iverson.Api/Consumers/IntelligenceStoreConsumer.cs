using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Grpc.Core;
using Iverson.Api.Schema;
using Iverson.Embeddings;
using Iverson.Events;
using Iverson.Sql;
using Iverson.Vector;
using Microsoft.Extensions.Options;
using Qdrant.Client;

namespace Iverson.Api.Consumers;

/// <summary>
/// Subscribes to entity.created and entity.updated events and ingests into Qdrant.
///
/// Two paths per entity:
///   VectorFields  — embeds the annotated string property → upserts as a named vector
///                   in the entity's main Qdrant collection.
///   ChunkFields   — splits the annotated string property into overlapping windows,
///                   embeds each window → upserts into {collection}_chunks with
///                   payload { "text": "...", "parent_id": "...", "chunk_index": N }.
///
/// Routing is gated on StoreTarget.Intelligence only — relation completeness does not
/// affect whether an entity goes to Qdrant.
/// </summary>
public sealed class IntelligenceStoreConsumer(
    IEventConsumer consumer,
    IVectorSchemaManager vectorSchema,
    IVectorWriteService vectorWrite,
    IEmbeddingService embedding,
    SchemaRegistry registry,
    IEntityRepository entities,
    DocumentRenderer documentRenderer,
    IntelligenceTenantScope tenantScope,
    IEnrichmentService enrichment,
    IOptions<EnrichmentServiceOptions> enrichmentOptions,
    ILogger<IntelligenceStoreConsumer> logger) : BackgroundService
{
    private const string GroupId = "iverson.consumer.intelligence";

    // How much of the parent text stands in for the object's summary when no summary exists
    // yet — which is always the case on first ingest, before the enricher's republish drives a
    // second, summary-conditioned pass.
    private const int ParentTextContextChars = 2000;

    // Tracks which collections have been ensured this session
    private readonly HashSet<string> _ensuredCollections = [];

    protected override Task ExecuteAsync(CancellationToken ct) =>
        ConsumerResilience.RunWithRestartAsync(
            () => consumer.ConsumeAsync(EntityTopics.Events, GroupId, DispatchAsync, ct),
            logger,
            "Intelligence",
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
        if (!ev.TargetStores.HasFlag(StoreTarget.Intelligence)) return;

        var schema = registry.Get(ev.TypeName);
        if (schema is null || schema.CollectionName is null)
        {
            logger.LogError(
                "[Intelligence] Dropped event — no schema registered for type={Type} key={Key}.",
                ev.TypeName, key);
            Activity.Current?
                .SetTag("dropped_event", true)
                .SetTag("dropped_event.reason", "schema_not_found")
                .SetTag("dropped_event.type", ev.TypeName);
            return;
        }

        JsonElement payload;
        try
        {
            using var doc = JsonDocument.Parse(ev.PayloadJson);
            payload = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new PoisonMessageException($"[Intelligence] Malformed payload JSON type={ev.TypeName} key={key}", ex);
        }

        var pointId = KeyToUlong(ev.Key);

        // Re-derive the ownership value from the authoritative Postgres row rather than
        // trusting the event payload's own value for it — the payload is unsigned JSON
        // and this value feeds Qdrant's read-time row authorization filtering (CSR #7).
        var ownerField = schema.Authorization?.OwnerField;
        var authoritativeOwnerValue = ownerField is not null
            ? await FetchAuthoritativeOwnerValueAsync(schema, ownerField, ev.Key, ct)
            : null;

        // Same re-derivation, for the tenant boundary (qdrant-tenant-collection-isolation):
        // the tenant value routes which physical Qdrant collection this point is written to,
        // so it must come from the authoritative Postgres row, not the unsigned event payload.
        // Computed unconditionally (not gated on VectorFields.Count > 0) because a chunks-only
        // schema needs it too — both the vector- and chunk-upsert blocks below reuse this value.
        var authoritativeTenantValue = schema.TenantColumn is not null
            ? await FetchAuthoritativeOwnerValueAsync(schema, schema.TenantColumn, ev.Key, ct)
            : null;

        // ── Named vector upsert (entity-level embeddings) ──────────────────────
        var objectPointWritten = false;
        if (schema.VectorFields.Count > 0)
        {
            var namedVectors = new Dictionary<string, float[]>(schema.VectorFields.Count);

            var embedTasks = schema.VectorFields
                .Select(vf => (vf, text: ExtractString(payload, vf.PropertyName)))
                .Where(x => !string.IsNullOrWhiteSpace(x.text))
                .Select(async x => (
                    vectorKey: $"{x.vf.PropertyName.ToSnakeCase()}_vector",
                    vector: await embedding.EmbedAsync(x.text!, ct)
                ))
                .ToList();

            var embedded = await Task.WhenAll(embedTasks);
            foreach (var (vectorKey, vec) in embedded)
                namedVectors[vectorKey] = vec;

            if (namedVectors.Count > 0)
            {
                var pointPayload = BuildObjectPointPayload(ev.Key, schema, payload, ownerField, authoritativeOwnerValue);
                var collectionName = tenantScope.ResolveCollectionName(schema.CollectionName, authoritativeTenantValue, isChunks: false);
                if (authoritativeTenantValue is not null)
                    await EnsureCollectionAsync(SchemaBuilder.ToCollectionSchema(schema) with { CollectionName = collectionName });

                using (RequestHeaders.Use("api-key", tenantScope.MintScopedApiKey(collectionName, readOnly: false)))
                {
                    await vectorWrite.UpsertNamedAsync(collectionName, pointId, namedVectors, pointPayload);
                }
                objectPointWritten = true;
                logger.LogInformation("[Intelligence] Upserted {Count} vector(s) for {Type}:{Key}",
                    namedVectors.Count, ev.TypeName, ev.Key);
            }
        }

        // ── Chunk upsert (passage-level RAG embeddings) ────────────────────────
        var centroids = new Dictionary<string, float[]>();
        if (schema.ChunkFields.Count > 0)
        {
            var chunksCollectionName = tenantScope.ResolveCollectionName(schema.CollectionName, authoritativeTenantValue, isChunks: true);
            if (authoritativeTenantValue is not null)
                await EnsureCollectionAsync(SchemaBuilder.ToChunkCollectionSchema(schema) with { CollectionName = chunksCollectionName });

            // Contextual prefixes are conditioned on the object's generated summary. It lives on
            // the authoritative row, so it is fetched at most once per event and only when some
            // chunk field actually asks for it and the global kill-switch is on.
            var contextualEnabled = enrichmentOptions.Value.Enabled
                                 && schema.ChunkFields.Any(cf => cf.Contextual);
            var summary = contextualEnabled
                ? await FetchSummaryAsync(schema, ev.Key, ct)
                : null;

            // Caps generative fan-out for contextual prefixes. Embedding calls stay unthrottled —
            // only the (far more expensive) generative calls are gated. See
            // EnrichmentServiceOptions.MaxConcurrentChunkPrefixes.
            using var prefixGate = new SemaphoreSlim(
                Math.Max(1, enrichmentOptions.Value.MaxConcurrentChunkPrefixes));

            using (RequestHeaders.Use("api-key", tenantScope.MintScopedApiKey(chunksCollectionName, readOnly: false)))
            {
                foreach (var cf in schema.ChunkFields)
                {
                    string? text;
                    if (cf.PropertyName == "Document")
                    {
                        // authoritativeTenantValue can be null independent of whether the type
                        // even declares a TenantColumn — FetchAuthoritativeOwnerValueAsync also
                        // returns null when the authoritative Postgres row is gone by the time
                        // this event is processed. That is still safe to render through: a null
                        // tenant becomes a SQL NULL RLS GUC, so relation fetches return zero rows
                        // and the document renders from payload scalars only — but it is a
                        // silently degraded document, so log it rather than assert it away.
                        if (authoritativeTenantValue is null)
                        {
                            logger.LogWarning(
                                "[Intelligence] Rendering document for type={Type} key={Key} with no " +
                                "authoritative tenant value — relation-backed placeholders will render empty.",
                                schema.TypeName.SanitizeForLog(), ev.Key.SanitizeForLog());
                        }

                        text = await documentRenderer.RenderAsync(
                            schema, payload, authoritativeTenantValue ?? string.Empty, ct);
                    }
                    else
                    {
                        text = ExtractString(payload, cf.PropertyName);
                    }

                    // Delete this field's stale chunk points before the empty-text guard below —
                    // not after. A field whose text has shrunk or become empty must not leave
                    // orphaned points from the previous write; scoping by parent_id AND field
                    // (rather than parent_id alone) keeps this from touching other chunk fields'
                    // points on the same parent.
                    var chunkFieldFilter = IntelligenceFilterBuilder.MatchParentIdAndField(ev.Key, cf.PropertyName);
                    await vectorWrite.DeleteByFilterAsync(chunksCollectionName, chunkFieldFilter);

                    if (string.IsNullOrWhiteSpace(text)) continue;

                    var vectorName = $"{cf.PropertyName.ToSnakeCase()}_vector";
                    var chunks     = SplitIntoChunks(text, cf.MaxTokens, cf.Overlap).ToList();

                    // No summary yet (always so on first ingest) — stand in a truncated slice of
                    // the parent text so the excerpt is still situated in *something*.
                    var documentContext = contextualEnabled && cf.Contextual
                        ? summary ?? text[..Math.Min(text.Length, ParentTextContextChars)]
                        : null;

                    var chunkTasks = chunks.Select(async chunk =>
                    {
                        var (chunkText, chunkIndex) = chunk;
                        var textToEmbed = documentContext is not null
                            ? await PrefixWithContextAsync(
                                  prefixGate, documentContext, chunkText, schema.TypeName, ev.Key, ct)
                            : chunkText;
                        var chunkVector = await embedding.EmbedAsync(textToEmbed, ct);
                        var chunkId     = ComputeChunkPointId(pointId, cf.PropertyName, chunkIndex);
                        return (chunkVector, chunkId, chunkText, chunkIndex);
                    }).ToList();

                    var chunkResults = await Task.WhenAll(chunkTasks);

                    // Filter degenerate vectors from the CENTROID INPUT ONLY. A zero-magnitude
                    // vector makes ComputeCentroid divide by zero and store a NaN centroid, which
                    // part 3 fuses into a NaN score that sinks the document to the bottom of every
                    // result set — silently. The chunk-point write loop below is UNCHANGED: every
                    // chunk keeps its own vector, degenerate ones included, because part 4b's
                    // diversifier already treats a NaN cosine as an absent signal.
                    var centroidInput = chunkResults
                        .Select(r => r.chunkVector)
                        .Where(v => !IsZeroMagnitude(v))
                        .ToList();

                    var degenerate = chunkResults.Length - centroidInput.Count;
                    if (degenerate > 0)
                        logger.LogWarning(
                            "[Intelligence] Dropped {Count} zero-magnitude chunk vector(s) from the centroid for {Type}:{Key} field={Field}",
                            degenerate,
                            ev.TypeName.SanitizeForLog(),
                            ev.Key.SanitizeForLog(),
                            cf.PropertyName.SanitizeForLog());

                    // No centroid at all when nothing survives — an ABSENT centroid is a state part 3
                    // handles; a NaN one is not. ComputeCentroid would also throw on an empty list.
                    //
                    // Consequence, accepted deliberately: the centroid-write block below is gated on
                    // `centroids.Count > 0`, and for a chunk-only type that block is ALSO what creates
                    // the object point and its payload (:286-293; objectPointWritten is set only in the
                    // plain-[IversonEmbedding] path at :150). So an entity whose every chunk vector is
                    // degenerate now gets no object point at all, where before it got one carrying a
                    // NaN centroid. Such an entity has no usable vector content either way.
                    //
                    // On an UPDATE, this same guard means: if the entity already has a stored centroid
                    // and a later Updated event's chunks all embed to zero, the write is skipped and
                    // Qdrant retains the previous, now-superseded centroid. Accepted deliberately — a
                    // stale finite centroid is strictly better than the NaN one the old code would have
                    // written, and it matches the pre-existing blank-text path (:184), which already
                    // leaves a stale centroid in place.
                    if (centroidInput.Count > 0)
                        centroids[$"{cf.PropertyName.ToSnakeCase()}_centroid"] = ComputeCentroid(centroidInput);

                    foreach (var (chunkVector, chunkId, chunkText, chunkIndex) in chunkResults)
                    {
                        var chunkPayload = new Dictionary<string, object>
                        {
                            ["text"]        = chunkText,
                            ["parent_id"]   = ev.Key,
                            ["field"]       = cf.PropertyName,
                            ["chunk_index"] = chunkIndex.ToString()
                        };
                        if (authoritativeOwnerValue is not null)
                            chunkPayload[schema.Authorization!.OwnerField!.ToCamelCase()] = authoritativeOwnerValue;

                        foreach (var name in schema.MetadataColumns)
                        {
                            if (ownerField is not null && string.Equals(name, ownerField, StringComparison.OrdinalIgnoreCase))
                                continue; // authoritative owner write above covers this key (CSR #7)
                            // No reserved-key guard needed here: SchemaBuilder rejects a metadata
                            // column whose camelCase name collides with a reserved chunk payload key
                            // at registration, so one cannot reach this loop.
                            var camelKey = name.ToCamelCase();
                            // ScalarColumns position: INCLUDE __TenantId. This lookup only resolves
                            // the SqlType of a column already named by MetadataColumns, so filtering
                            // it out here would change nothing that MetadataColumns does not already
                            // decide — and if the tenant column ever does need denormalizing onto a
                            // chunk point, it must find its real TEXT type rather than fall back.
                            var sqlType = schema.ScalarColumns.FirstOrDefault(c =>
                                string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))?.SqlType ?? "TEXT";
                            var val = ExtractTypedValue(payload, name, sqlType);
                            if (val is not null) chunkPayload[camelKey] = val;
                        }

                        await vectorWrite.UpsertNamedAsync(
                            chunksCollectionName,
                            chunkId,
                            new Dictionary<string, float[]> { [vectorName] = chunkVector },
                            chunkPayload);
                    }

                    logger.LogInformation("[Intelligence] Ingested {Count} chunk(s) for {Type}:{Key} field={Field}",
                        chunks.Count, ev.TypeName, ev.Key, cf.PropertyName);
                }
            }
        }

        // ── Centroid write (document-level signal derived from this event's chunks) ───────
        //
        // Never branches on VectorFields.Count — an entity can declare vector fields whose text
        // is blank on this event, in which case the object block above never runs even though
        // the object point already exists from a prior event. objectPointWritten is only a fast
        // path (skip the doomed update attempt right after we just upserted the point ourselves);
        // it is not sufficient on its own to pick the write mode, because "the object block
        // didn't run this event" does not imply "the point doesn't exist." Qdrant's upsert nulls
        // every unspecified named vector, so upserting straight past an existing point here would
        // silently destroy its *_vector values. UpdateNamedVectorsAsync is therefore always tried
        // first when we didn't just write the point ourselves; only a genuine "point not found"
        // (surfaced by Qdrant as gRPC NotFound) falls back to upsert.
        // Gated on authoritativeTenantValue is not null, unlike the chunk upserts above (:239,
        // ungated) — inherited asymmetry, not accidental: on the documented delete-then-recreate
        // race (authoritative row missing), chunks are still written but the centroid is silently
        // dropped. Plan-conformant; not changed here.
        if (centroids.Count > 0 && authoritativeTenantValue is not null)
        {
            var collectionName = tenantScope.ResolveCollectionName(schema.CollectionName, authoritativeTenantValue, isChunks: false);
            if (!objectPointWritten)
                await EnsureCollectionAsync(SchemaBuilder.ToCollectionSchema(schema) with { CollectionName = collectionName });

            using (RequestHeaders.Use("api-key", tenantScope.MintScopedApiKey(collectionName, readOnly: false)))
            {
                // objectPointWritten is a fast path only: when we just wrote the object point
                // ourselves this event, the update is certain to succeed, so there's no need to
                // wrap it in the try/catch. Otherwise the point's existence is genuinely unknown
                // (it may survive from a prior event, or may never have been written), so the
                // update is attempted and only a real NotFound falls back to upsert.
                if (objectPointWritten)
                {
                    await vectorWrite.UpdateNamedVectorsAsync(collectionName, pointId, centroids);
                    logger.LogInformation("[Intelligence] Updated {Count} centroid(s) for {Type}:{Key} (object point written this event)",
                        centroids.Count, ev.TypeName, ev.Key);
                }
                else if (!await TryUpdateNamedVectorsAsync(collectionName, pointId, centroids))
                {
                    await vectorWrite.UpsertNamedAsync(
                        collectionName,
                        pointId,
                        centroids,
                        BuildObjectPointPayload(ev.Key, schema, payload, ownerField, authoritativeOwnerValue));
                    logger.LogInformation("[Intelligence] Upserted {Count} centroid(s) for {Type}:{Key} (object point did not exist)",
                        centroids.Count, ev.TypeName, ev.Key);
                }
                else
                {
                    logger.LogInformation("[Intelligence] Updated {Count} centroid(s) for {Type}:{Key} (object point already existed)",
                        centroids.Count, ev.TypeName, ev.Key);
                }
            }
        }
    }

    // Attempts the partial-update path for the centroid write. Returns true on success. Returns
    // false only when Qdrant reports the point does not exist yet (gRPC NotFound) — the signal
    // the caller uses to fall back to an upsert. Any other RPC failure propagates: this must not
    // swallow errors unrelated to point existence.
    private async Task<bool> TryUpdateNamedVectorsAsync(
        string collectionName, ulong pointId, IReadOnlyDictionary<string, float[]> centroids)
    {
        try
        {
            await vectorWrite.UpdateNamedVectorsAsync(collectionName, pointId, centroids);
            return true;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return false;
        }
    }

    // Builds the object-level point payload — key, vector-field text, scalar columns (owner
    // column re-derived from the authoritative row per CSR #7), and FK columns. Shared by the
    // named-vector upsert and the centroid write, which independently need the same payload
    // when the object block did not already write the point this event.
    private static Dictionary<string, object> BuildObjectPointPayload(
        string key,
        SchemaDescriptor schema,
        JsonElement payload,
        string? ownerField,
        string? authoritativeOwnerValue)
    {
        var pointPayload = new Dictionary<string, object> { ["key"] = key };
        foreach (var vf in schema.VectorFields)
        {
            var fieldText = ExtractString(payload, vf.PropertyName);
            if (!string.IsNullOrWhiteSpace(fieldText))
                pointPayload[vf.PropertyName.ToCamelCase()] = fieldText;
        }
        // ScalarColumns position: INCLUDE __TenantId. This builds the Qdrant point payload, which
        // is a projection of the stored row, not a client-facing surface — the tenant value must
        // reach the vector store so points carry a tenant discriminator alongside the collection
        // routing. Read-side exposure is governed at the search RPCs, not here.
        foreach (var col in schema.ScalarColumns)
        {
            var isOwnerColumn = ownerField is not null &&
                string.Equals(col.Name, ownerField, StringComparison.OrdinalIgnoreCase);
            var val = isOwnerColumn
                ? authoritativeOwnerValue
                : ExtractTypedValue(payload, col.Name, col.SqlType);
            if (val is not null) pointPayload[col.Name.ToCamelCase()] = val;
        }
        foreach (var fk in schema.FkColumns)
        {
            var val = ExtractTypedValue(payload, fk.ColumnName, "TEXT");
            if (val is not null) pointPayload[fk.ColumnName.ToCamelCase()] = val;
        }
        return pointPayload;
    }

    // Mirrors ComputeCentroid's own magnitude computation. A vector whose components are small
    // enough to underflow when squared also lands here, since the accumulated magnitude is then
    // exactly zero — which is the case that would divide to Infinity rather than NaN.
    private static bool IsZeroMagnitude(float[] vector)
    {
        float magnitude = 0;
        foreach (var component in vector)
            magnitude += component * component;
        return magnitude == 0;
    }

    // L2-normalizes each input vector and returns their componentwise mean, without
    // re-normalizing the result: Qdrant normalizes on store under Distance.Cosine, and cosine
    // similarity is scale-invariant, so a second normalization here would buy nothing.
    // No zero-magnitude guard: the caller filters zero-magnitude vectors out of the input before
    // calling (see IsZeroMagnitude at the chunk-embedding site), so the invariant is enforced at
    // the boundary rather than assumed here. The earlier reasoning — that blank text is skipped
    // upstream and SplitIntoChunks always yields a chunk — was about the input *text*, and never
    // covered the actual failure mode: the embedding model returning a zero vector for non-blank
    // text, which would divide by zero and produce a NaN centroid.
    // Assumes every input vector shares vectors[0].Length (one embedding model per chunk field,
    // so all chunks for a given field are the same dimensionality today). A shorter input would
    // throw; a longer one would be silently truncated in the sum while still contributing its
    // full magnitude to the normalization — unreachable under the current one-model-per-field
    // invariant, so no runtime guard is added.
    internal static float[] ComputeCentroid(IReadOnlyList<float[]> vectors)
    {
        var dims = vectors[0].Length;
        var sum = new float[dims];

        foreach (var vector in vectors)
        {
            float magnitude = 0;
            foreach (var component in vector)
                magnitude += component * component;
            magnitude = MathF.Sqrt(magnitude);

            for (var i = 0; i < dims; i++)
                sum[i] += vector[i] / magnitude;
        }

        var mean = new float[dims];
        for (var i = 0; i < dims; i++)
            mean[i] = sum[i] / vectors.Count;

        return mean;
    }

    internal async Task HandleDeleteAsync(string key, string value, CancellationToken ct)
    {
        var ev = Deserialize(key, value);
        if (!ev.TargetStores.HasFlag(StoreTarget.Intelligence)) return;

        var schema = registry.Get(ev.TypeName);
        if (schema?.CollectionName is null)
        {
            logger.LogError(
                "[Intelligence] Dropped event — no schema registered for type={Type} key={Key}.",
                ev.TypeName, ev.Key);
            Activity.Current?
                .SetTag("dropped_event", true)
                .SetTag("dropped_event.reason", "schema_not_found")
                .SetTag("dropped_event.type", ev.TypeName);
            return;
        }

        // Source the tenant value from the pre-delete row snapshot ObjectMappingGrpcService.Delete
        // published in ev.PayloadJson — the row is already gone from Postgres by the time a delete
        // event is consumed, so there is no authoritative row left to re-fetch (unlike HandleAsync).
        JsonElement payload;
        try
        {
            using var doc = JsonDocument.Parse(ev.PayloadJson);
            payload = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new PoisonMessageException($"[Intelligence] Malformed payload JSON type={ev.TypeName} key={key}", ex);
        }

        var tenantValue = schema.TenantColumn is not null ? ExtractString(payload, schema.TenantColumn) : null;

        var pointId = KeyToUlong(ev.Key);

        var collectionName = tenantScope.ResolveCollectionName(schema.CollectionName, tenantValue, isChunks: false);
        using (RequestHeaders.Use("api-key", tenantScope.MintScopedApiKey(collectionName, readOnly: false)))
        {
            await vectorWrite.DeleteAsync(collectionName, pointId);
        }

        if (schema.ChunkFields.Count > 0)
        {
            var chunksCollectionName = tenantScope.ResolveCollectionName(schema.CollectionName, tenantValue, isChunks: true);
            var chunkFilter = IntelligenceFilterBuilder.MatchParentId(ev.Key);
            using (RequestHeaders.Use("api-key", tenantScope.MintScopedApiKey(chunksCollectionName, readOnly: false)))
            {
                await vectorWrite.DeleteByFilterAsync(chunksCollectionName, chunkFilter);
            }
        }

        logger.LogInformation("[Intelligence] Deleted vector for {Type}:{Key}", ev.TypeName.SanitizeForLog(), ev.Key);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Re-derives the ownership value from the authoritative Postgres row instead of trusting
    // the event payload's own value for it (CSR #7 — event JSON is unsigned and this value
    // feeds Qdrant's read-time row authorization filtering). Fails closed: if the row can't be
    // found (e.g. a delete-then-recreate race), the owner value is treated as absent rather
    // than falling back to the unvalidated payload value.
    private async Task<string?> FetchAuthoritativeOwnerValueAsync(
        SchemaDescriptor schema, string ownerField, string key, CancellationToken ct)
    {
        var rowJson = await entities.FetchByKeyAsync(SchemaBuilder.ToTableSchema(schema), key);
        if (rowJson is null)
        {
            logger.LogWarning(
                "[Intelligence] Owner re-derivation found no authoritative row for type={Type} key={Key} — omitting owner value.",
                schema.TypeName.SanitizeForLog(), key);
            return null;
        }

        using var doc = JsonDocument.Parse(rowJson);
        return ExtractString(doc.RootElement, ownerField);
    }

    // Locates the object's summary via the type's EnrichmentTargets and reads it out of the
    // authoritative row. Returns null when the type declares no summary target, when the row is
    // gone, or when the column has not been filled in yet — every one of which simply means the
    // caller falls back to the parent-text context.
    private async Task<string?> FetchSummaryAsync(SchemaDescriptor schema, string key, CancellationToken ct)
    {
        var summaryTarget = schema.EnrichmentTargets
            .FirstOrDefault(t => t.Kind == EnrichmentKind.Summary);
        if (summaryTarget is null) return null;

        try
        {
            var rowJson = await entities.FetchByKeyAsync(SchemaBuilder.ToTableSchema(schema), key);
            if (rowJson is null) return null;

            using var doc = JsonDocument.Parse(rowJson);
            var summary = ExtractString(doc.RootElement, summaryTarget.ColumnName);
            return string.IsNullOrWhiteSpace(summary) ? null : summary;
        }
        catch (Exception ex)
        {
            // Same best-effort contract as the generation call itself: an unavailable summary
            // must never cost the object its chunks.
            logger.LogWarning(ex,
                "[Intelligence] Could not read summary for {Type}:{Key} — using parent-text context.",
                schema.TypeName.SanitizeForLog(), key);
            return null;
        }
    }

    // Generates the situating sentence and prepends it to the chunk text that gets embedded.
    // Caught per chunk (spec §6): a generation failure must leave the object's projection intact,
    // and must not cost the *other* chunks of the same field their prefixes.
    private async Task<string> PrefixWithContextAsync(
        SemaphoreSlim gate, string documentContext, string chunkText, string typeName, string key,
        CancellationToken ct)
    {
        try
        {
            await gate.WaitAsync(ct);
            string? prefix;
            try
            {
                prefix = await enrichment.GenerateAsync(
                    string.Format(EnrichmentPrompts.ChunkContext, documentContext, chunkText), ct);
            }
            finally
            {
                gate.Release();
            }

            return string.IsNullOrWhiteSpace(prefix) ? chunkText : $"{prefix.Trim()}\n\n{chunkText}";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[Intelligence] Contextual prefix generation failed for {Type}:{Key} — embedding the chunk unprefixed.",
                typeName.SanitizeForLog(), key);
            return chunkText;
        }
    }

    private async Task EnsureCollectionAsync(CollectionSchema collectionSchema)
    {
        if (_ensuredCollections.Contains(collectionSchema.CollectionName)) return;
        await vectorSchema.ApplyCollectionAsync(collectionSchema);
        _ensuredCollections.Add(collectionSchema.CollectionName);
    }

    // Splits text into overlapping windows. Token approximation: 1 token ≈ 4 characters.
    private static IEnumerable<(string Text, int Index)> SplitIntoChunks(string text, int maxTokens, int overlap)
    {
        var maxChars     = maxTokens * 4;
        var overlapChars = overlap * 4;
        var step         = Math.Max(maxChars - overlapChars, maxChars / 2);

        var start = 0;
        var index = 0;

        while (start < text.Length)
        {
            var end = Math.Min(start + maxChars, text.Length);

            // Extend to word boundary if possible
            if (end < text.Length && !char.IsWhiteSpace(text[end]))
            {
                var ws = text.LastIndexOf(' ', end, Math.Min(end - start, 50));
                if (ws > start) end = ws;
            }

            yield return (text[start..end].Trim(), index++);
            start += step;
        }
    }

    // Deterministic ulong from a string key (UUID → lower 8 bytes of Guid bytes).
    // internal (not private) because ObjectSearchGrpcService must derive the SAME point id
    // from a chunk's parent_id payload value when it fetches parent centroids for re-ranking;
    // both sides have to agree on this function. Still NonPublic, so the reflection-based
    // tests binding it on typeof(IntelligenceStoreConsumer) keep working.
    internal static ulong KeyToUlong(string key)
    {
        if (Guid.TryParse(key, out var g))
        {
            var bytes = g.ToByteArray();
            return BitConverter.ToUInt64(bytes, 8);
        }
        // Non-GUID keys are unreachable today (keys are server-generated UUIDv7), but use the
        // same stable FNV-1a hash as ComputeChunkPointId — not string.GetHashCode(), which
        // .NET randomizes per process — since this value feeds ComputeChunkPointId's parentId.
        return FnvHash(key);
    }

    // Combines parent ID + field name + chunk index into a collision-resistant ulong.
    // Uses FNV-1a (not string.GetHashCode()) because .NET randomizes string.GetHashCode()
    // per process as a hash-flooding mitigation — the old implementation produced a
    // different chunk point ID for the same (parentId, fieldName, chunkIndex) on every
    // process restart, silently duplicating chunk content in Qdrant on every update that
    // crossed a restart boundary instead of overwriting the existing point.
    private static ulong ComputeChunkPointId(ulong parentId, string fieldName, int chunkIndex) =>
        parentId ^ ((FnvHash(fieldName) * 1000003UL + (ulong)chunkIndex) * 0x9E3779B97F4A7C15UL);

    private static ulong FnvHash(string s)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime       = 1099511628211UL;
        var hash = offsetBasis;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(s))
        {
            hash ^= b;
            hash *= prime;
        }
        return hash;
    }

    private static string? ExtractString(JsonElement payload, string propertyName)
    {
        if (payload.TryGetProperty(propertyName, out var v))
            return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();

        // Try camelCase fallback
        var camel = char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
        if (payload.TryGetProperty(camel, out var vc))
            return vc.ValueKind == JsonValueKind.String ? vc.GetString() : vc.ToString();

        return null;
    }

    private static object? ExtractTypedValue(JsonElement payload, string propertyName, string sqlType)
    {
        if (!payload.TryGetProperty(propertyName, out var v))
        {
            var camel = char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
            if (!payload.TryGetProperty(camel, out v)) return null;
        }
        if (v.ValueKind == JsonValueKind.Null) return null;

        var normalized = sqlType.ToUpperInvariant();

        // An array column's SqlType is the element type with a trailing "[]" ("INTEGER[]",
        // "TIMESTAMPTZ[]", ...). Its payload index is built from the ELEMENT kind, so the value
        // has to reach Qdrant as a real list of element-typed values — not the raw JSON text,
        // which would silently be unfilterable under an integer/datetime index.
        if (normalized.EndsWith("[]", StringComparison.Ordinal))
        {
            if (v.ValueKind != JsonValueKind.Array) return null;

            var elementType = normalized[..^2];
            var items       = new List<object>();

            // An element that will not coerce is skipped, exactly as a failing scalar yields null.
            foreach (var element in v.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Null) continue;
                var coerced = CoerceElement(element, elementType);
                if (coerced is not null) items.Add(coerced);
            }

            return items;
        }

        return CoerceElement(v, normalized);
    }

    // Per-element coercion shared verbatim by the scalar and array paths — an array element must
    // land in the SAME form its scalar counterpart would, or the read side stops matching it.
    private static object? CoerceElement(JsonElement v, string normalizedSqlType) =>
        normalizedSqlType switch
        {
            // The ValueKind guard is load-bearing: TryGetInt64/TryGetDouble THROW (they do not
            // return false) when the element is not a JSON number, which would turn one badly
            // typed field into a poisoned message for the whole event.
            "INTEGER" or "BIGINT"         => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null,
            "REAL" or "DOUBLE PRECISION"  => v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null,
            "BOOLEAN"                     => v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null,
            // Canonicalize timestamps to UTC round-trip ("o") form so equality filters — which
            // compare payload strings verbatim — match any input naming the same INSTANT, whatever
            // offset the client expressed it in. AdjustToUniversal normalizes the offset;
            // AssumeUniversal makes an offset-LESS value mean UTC rather than the pod's local
            // timezone, so two pods with different TZ settings canonicalize identically.
            // IntelligenceFilterBuilder.Canonicalize applies the SAME rule on the read side.
            // ToQdrantValue writes the DateTimeOffset out in "o" form. A value that will not parse
            // yields null, so the column is simply absent from the payload rather than stored in a
            // format nothing can match.
            "TIMESTAMPTZ" or "DATETIME"   =>
                v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(
                    v.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto)
                    ? dto
                    : null,
            _                             => v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()
        };

    private static EntityEvent Deserialize(string key, string value)
    {
        EntityEvent? ev;
        try
        {
            ev = JsonSerializer.Deserialize<EntityEvent>(value, s_jsonOptions);
        }
        catch (JsonException ex)
        {
            throw new PoisonMessageException($"[Intelligence] Malformed event JSON key={key}", ex);
        }

        return ev ?? throw new PoisonMessageException($"[Intelligence] Event deserialized to null key={key}");
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}
