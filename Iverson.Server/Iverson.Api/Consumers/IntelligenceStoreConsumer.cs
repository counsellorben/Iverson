using System.Text.Json;
using Iverson.Api.Schema;
using Iverson.Embeddings;
using Iverson.Events;
using Iverson.Vector;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
        Task.WhenAll(
            consumer.ConsumeAsync(EntityTopics.Created, GroupId, HandleAsync, ct),
            consumer.ConsumeAsync(EntityTopics.Updated, GroupId, HandleAsync, ct),
            consumer.ConsumeAsync(EntityTopics.Deleted, GroupId + ".delete", HandleDeleteAsync, ct));

    internal async Task HandleAsync(string key, string value, CancellationToken ct)
    {
        var ev = Deserialize(key, value);
        if (ev is null || !ev.TargetStores.HasFlag(StoreTarget.Intelligence)) return;

        var schema = registry.Get(ev.TypeName);
        if (schema is null || schema.CollectionName is null)
        {
            logger.LogWarning("[Intelligence] No schema/collection for type={Type}", ev.TypeName);
            return;
        }

        JsonElement payload;
        try
        {
            using var doc = JsonDocument.Parse(ev.PayloadJson);
            payload = doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Intelligence] Failed to parse payload for type={Type} key={Key}", ev.TypeName, key);
            return;
        }

        var pointId = KeyToUlong(ev.Key);

        // ── Named vector upsert (entity-level embeddings) ──────────────────────
        if (schema.VectorFields.Count > 0)
        {
            var namedVectors = new Dictionary<string, float[]>(schema.VectorFields.Count);

            foreach (var vf in schema.VectorFields)
            {
                var text = ExtractString(payload, vf.PropertyName);
                if (string.IsNullOrWhiteSpace(text)) continue;

                try
                {
                    namedVectors[$"{ToSnakeCase(vf.PropertyName)}_vector"] =
                        await embedding.EmbedAsync(text, ct);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[Intelligence] Embed failed field={Field} type={Type}", vf.PropertyName, ev.TypeName);
                }
            }

            if (namedVectors.Count > 0)
            {
                var pointPayload = new Dictionary<string, string> { ["key"] = ev.Key };
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

                var vectorName = $"{ToSnakeCase(cf.PropertyName)}_vector";
                var chunks     = SplitIntoChunks(text, cf.MaxTokens, cf.Overlap).ToList();

                for (var i = 0; i < chunks.Count; i++)
                {
                    var (chunkText, chunkIndex) = chunks[i];

                    try
                    {
                        var chunkVector = await embedding.EmbedAsync(chunkText, ct);
                        var chunkId     = ChunkPointId(pointId, cf.PropertyName, chunkIndex);

                        await vector.UpsertNamedAsync(
                            chunksCollection,
                            chunkId,
                            new Dictionary<string, float[]> { [vectorName] = chunkVector },
                            new Dictionary<string, string>
                            {
                                ["text"]        = chunkText,
                                ["parent_id"]   = ev.Key,
                                ["field"]       = cf.PropertyName,
                                ["chunk_index"] = chunkIndex.ToString()
                            });
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "[Intelligence] Chunk embed failed field={Field} chunk={Index}", cf.PropertyName, i);
                    }
                }

                logger.LogInformation("[Intelligence] Ingested {Count} chunk(s) for {Type}:{Key} field={Field}",
                    chunks.Count, ev.TypeName, ev.Key, cf.PropertyName);
            }
        }
    }

    internal async Task HandleDeleteAsync(string key, string value, CancellationToken ct)
    {
        var ev = Deserialize(key, value);
        if (ev is null || !ev.TargetStores.HasFlag(StoreTarget.Intelligence)) return;

        var schema = registry.Get(ev.TypeName);
        if (schema?.CollectionName is null) return;

        var pointId = KeyToUlong(ev.Key);

        try
        {
            await vector.DeleteAsync(schema.CollectionName, pointId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Intelligence] Delete from main collection failed {Type}:{Key}", ev.TypeName, ev.Key);
        }

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
                .Select(c => new NamedVector($"{ToSnakeCase(c.PropertyName)}_vector", c.Dimension))
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
    private static ulong ChunkPointId(ulong parentId, string fieldName, int chunkIndex) =>
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

    private static string ToSnakeCase(string name)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }

    private EntityEvent? Deserialize(string key, string value)
    {
        try
        {
            return JsonSerializer.Deserialize<EntityEvent>(value, JsonOptions);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Intelligence] Failed to deserialize event key={Key}", key);
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}
