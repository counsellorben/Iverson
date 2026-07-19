using System.Diagnostics;
using System.Text.Json;
using Iverson.Api.Schema;
using Iverson.Embeddings;
using Iverson.Events;
using Iverson.Sql;
using Iverson.Vector;

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
    ILogger<IntelligenceStoreConsumer> logger) : BackgroundService
{
    private const string GroupId = "iverson.consumer.intelligence";

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

        // ── Named vector upsert (entity-level embeddings) ──────────────────────
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
                var pointPayload = new Dictionary<string, object> { ["key"] = ev.Key };
                foreach (var vf in schema.VectorFields)
                {
                    var fieldText = ExtractString(payload, vf.PropertyName);
                    if (!string.IsNullOrWhiteSpace(fieldText))
                        pointPayload[vf.PropertyName.ToCamelCase()] = fieldText;
                }
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
                await vectorWrite.UpsertNamedAsync(schema.CollectionName, pointId, namedVectors, pointPayload);
                logger.LogInformation("[Intelligence] Upserted {Count} vector(s) for {Type}:{Key}",
                    namedVectors.Count, ev.TypeName, ev.Key);
            }
        }

        // ── Chunk upsert (passage-level RAG embeddings) ────────────────────────
        if (schema.ChunkFields.Count > 0)
        {
            var chunksCollection = schema.CollectionName + "_chunks";
            await EnsureCollectionAsync(new CollectionSchema(
                chunksCollection,
                schema.ChunkFields
                    .Select(c => new NamedVector($"{c.PropertyName.ToSnakeCase()}_vector", c.Dimension))
                    .ToList(),
                [new PayloadIndex("parent_id", PayloadIndexKind.Keyword)]));

            foreach (var cf in schema.ChunkFields)
            {
                var text = ExtractString(payload, cf.PropertyName);
                if (string.IsNullOrWhiteSpace(text)) continue;

                var vectorName = $"{cf.PropertyName.ToSnakeCase()}_vector";
                var chunks     = SplitIntoChunks(text, cf.MaxTokens, cf.Overlap).ToList();

                var chunkTasks = chunks.Select(async chunk =>
                {
                    var (chunkText, chunkIndex) = chunk;
                    var chunkVector = await embedding.EmbedAsync(chunkText, ct);
                    var chunkId     = ComputeChunkPointId(pointId, cf.PropertyName, chunkIndex);
                    return (chunkVector, chunkId, chunkText, chunkIndex);
                }).ToList();

                var chunkResults = await Task.WhenAll(chunkTasks);

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

                    await vectorWrite.UpsertNamedAsync(
                        chunksCollection,
                        chunkId,
                        new Dictionary<string, float[]> { [vectorName] = chunkVector },
                        chunkPayload);
                }

                logger.LogInformation("[Intelligence] Ingested {Count} chunk(s) for {Type}:{Key} field={Field}",
                    chunks.Count, ev.TypeName, ev.Key, cf.PropertyName);
            }
        }
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

        var pointId = KeyToUlong(ev.Key);

        await vectorWrite.DeleteAsync(schema.CollectionName, pointId);

        if (schema.ChunkFields.Count > 0)
        {
            var chunkFilter = QdrantFilterBuilder.MatchParentId(ev.Key);
            await vectorWrite.DeleteByFilterAsync(schema.CollectionName + "_chunks", chunkFilter);
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

    // Deterministic ulong from a string key (UUID → lower 8 bytes of Guid bytes)
    private static ulong KeyToUlong(string key)
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

        return sqlType.ToUpperInvariant() switch
        {
            "INTEGER" or "BIGINT"         => v.TryGetInt64(out var l) ? l : null,
            "REAL" or "DOUBLE PRECISION"  => v.TryGetDouble(out var d) ? d : null,
            "BOOLEAN"                     => v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null,
            _                             => v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()
        };
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
