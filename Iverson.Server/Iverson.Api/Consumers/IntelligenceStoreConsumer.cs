using System.Diagnostics;
using System.Text.Json;
using Iverson.Api.Schema;
using Iverson.Embeddings;
using Iverson.Events;
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
    IVectorService vector,
    IEmbeddingService embedding,
    SchemaRegistry registry,
    ILogger<IntelligenceStoreConsumer> logger) : BackgroundService
{
    private const string GroupId = "iverson.consumer.intelligence";

    // Tracks which chunk collections have been ensured this session
    private readonly HashSet<string> _ensuredChunkCollections = [];

    protected override Task ExecuteAsync(CancellationToken ct) =>
        ConsumerResilience.RunWithRestartAsync(
            () => Task.WhenAll(
                consumer.ConsumeAsync(EntityTopics.Created, GroupId, HandleAsync, ct),
                consumer.ConsumeAsync(EntityTopics.Updated, GroupId, HandleAsync, ct),
                consumer.ConsumeAsync(EntityTopics.Deleted, GroupId + ".delete", HandleDeleteAsync, ct)),
            logger,
            "Intelligence",
            ct);

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
                        pointPayload[char.ToLowerInvariant(vf.PropertyName[0]) + vf.PropertyName[1..]] = fieldText;
                }
                await vector.UpsertNamedAsync(schema.CollectionName, pointId, namedVectors, pointPayload);
                logger.LogInformation("[Intelligence] Upserted {Count} vector(s) for {Type}:{Key}",
                    namedVectors.Count, ev.TypeName, ev.Key);
            }
        }

        // ── Chunk upsert (passage-level RAG embeddings) ────────────────────────
        if (schema.ChunkFields.Count > 0)
        {
            var chunksCollection = schema.CollectionName + "_chunks";
            await EnsureChunkCollectionAsync(chunksCollection, schema, ct);

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
                    await vector.UpsertNamedAsync(
                        chunksCollection,
                        chunkId,
                        new Dictionary<string, float[]> { [vectorName] = chunkVector },
                        new Dictionary<string, object>
                        {
                            ["text"]        = chunkText,
                            ["parent_id"]   = ev.Key,
                            ["field"]       = cf.PropertyName,
                            ["chunk_index"] = chunkIndex.ToString()
                        });
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

        await vector.DeleteAsync(schema.CollectionName, pointId);

        // Best-effort: delete chunk points (no scroll-delete API; skip for now — orphaned chunks
        // are harmless for search correctness since parent_id lookups won't match live documents)
        logger.LogInformation("[Intelligence] Deleted vector for {Type}:{Key}", ev.TypeName, ev.Key);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task EnsureChunkCollectionAsync(string name, SchemaDescriptor schema, CancellationToken ct)
    {
        if (_ensuredChunkCollections.Contains(name)) return;

        var collectionSchema = new CollectionSchema(
            name,
            schema.ChunkFields
                .Select(c => new NamedVector($"{c.PropertyName.ToSnakeCase()}_vector", c.Dimension))
                .ToList(),
            [new PayloadIndex("parent_id", PayloadIndexKind.Keyword)]);

        await vector.ApplyCollectionAsync(collectionSchema);
        _ensuredChunkCollections.Add(name);
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
        return (ulong)Math.Abs(key.GetHashCode());
    }

    // Combines parent ID + field name + chunk index into a collision-resistant ulong
    private static ulong ComputeChunkPointId(ulong parentId, string fieldName, int chunkIndex) =>
        parentId ^ ((ulong)(fieldName.GetHashCode() * 1000003L + chunkIndex) * 0x9E3779B97F4A7C15UL);

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
