using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using QdrantVector = Qdrant.Client.Grpc.Vector;

namespace Iverson.Vector;

public class QdrantVectorService(
    QdrantClient client,
    ILogger<QdrantVectorService> logger) : IVectorService
{
    public async Task EnsureCollectionAsync(string collectionName, ulong vectorSize)
    {
        using var activity = Telemetry.Source.StartActivity("qdrant.ensure_collection", ActivityKind.Client);
        activity?.SetTag("db.system", "qdrant");
        activity?.SetTag("qdrant.collection", collectionName);
        activity?.SetTag("qdrant.vector_size", vectorSize);

        var collections = await client.ListCollectionsAsync();
        if (collections.Any(c => c == collectionName))
        {
            activity?.SetTag("qdrant.already_existed", true);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return;
        }

        logger.LogInformation("Creating Qdrant collection {Collection} with vector size {Size}", collectionName, vectorSize);

        await client.CreateCollectionAsync(collectionName, new VectorParams
        {
            Size = vectorSize,
            Distance = Distance.Cosine
        });

        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    public async Task UpsertAsync(
        string collectionName,
        ulong id,
        float[] vector,
        IReadOnlyDictionary<string, object>? payload = null)
    {
        using var activity = Telemetry.Source.StartActivity("qdrant.upsert", ActivityKind.Client);
        activity?.SetTag("db.system", "qdrant");
        activity?.SetTag("qdrant.collection", collectionName);
        activity?.SetTag("qdrant.point_id", id);
        activity?.SetTag("qdrant.vector_dims", vector.Length);

        var point = new PointStruct { Id = id, Vectors = vector };

        if (payload is not null)
            foreach (var (key, value) in payload)
                point.Payload[key] = ToQdrantValue(value);

        await client.UpsertAsync(collectionName, [point]);
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    public async Task UpsertNamedAsync(
        string collectionName,
        ulong id,
        IReadOnlyDictionary<string, float[]> namedVectors,
        IReadOnlyDictionary<string, object>? payload = null)
    {
        using var activity = Telemetry.Source.StartActivity("qdrant.upsert_named", ActivityKind.Client);
        activity?.SetTag("db.system", "qdrant");
        activity?.SetTag("qdrant.collection", collectionName);
        activity?.SetTag("qdrant.point_id", id);
        activity?.SetTag("qdrant.vector_count", namedVectors.Count);

        var named = new NamedVectors();
        foreach (var (name, data) in namedVectors)
            named.Vectors[name] = data;

        var point = new PointStruct
        {
            Id      = id,
            Vectors = new Vectors { Vectors_ = named }
        };

        if (payload is not null)
            foreach (var (key, value) in payload)
                point.Payload[key] = ToQdrantValue(value);

        await client.UpsertAsync(collectionName, [point]);
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string collectionName,
        float[] queryVector,
        ulong limit = 10)
    {
        using var activity = Telemetry.Source.StartActivity("qdrant.search", ActivityKind.Client);
        activity?.SetTag("db.system", "qdrant");
        activity?.SetTag("qdrant.collection", collectionName);
        activity?.SetTag("qdrant.limit", limit);
        activity?.SetTag("qdrant.vector_dims", queryVector.Length);

        var results = await client.SearchAsync(collectionName, queryVector, limit: limit, payloadSelector: true);

        activity?.SetTag("qdrant.result_count", results.Count);
        activity?.SetStatus(ActivityStatusCode.Ok);

        return results.Select(r => new VectorSearchResult(
            r.Id.Num,
            r.Score,
            r.Payload.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.StringValue)
        )).ToList();
    }

    public async Task<IReadOnlyList<VectorSearchResult>> SearchNamedAsync(
        string collectionName,
        string vectorName,
        float[] queryVector,
        ulong limit = 10)
    {
        using var activity = Telemetry.Source.StartActivity("qdrant.search_named", ActivityKind.Client);
        activity?.SetTag("db.system", "qdrant");
        activity?.SetTag("qdrant.collection", collectionName);
        activity?.SetTag("qdrant.vector_name", vectorName);
        activity?.SetTag("qdrant.limit", limit);

        var results = await client.SearchAsync(
            collectionName,
            queryVector,
            limit:           limit,
            payloadSelector: true,
            vectorName:      vectorName);

        activity?.SetTag("qdrant.result_count", results.Count);
        activity?.SetStatus(ActivityStatusCode.Ok);

        return results.Select(r => new VectorSearchResult(
            r.Id.Num,
            r.Score,
            r.Payload.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.StringValue)
        )).ToList();
    }

    public async Task DeleteAsync(string collectionName, ulong id)
    {
        using var activity = Telemetry.Source.StartActivity("qdrant.delete", ActivityKind.Client);
        activity?.SetTag("db.system", "qdrant");
        activity?.SetTag("qdrant.collection", collectionName);
        activity?.SetTag("qdrant.point_id", id);

        await client.DeleteAsync(collectionName, id);
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    public async Task ApplyCollectionAsync(CollectionSchema schema)
    {
        using var activity = Telemetry.Source.StartActivity("qdrant.apply_collection", ActivityKind.Client);
        activity?.SetTag("db.system", "qdrant");
        activity?.SetTag("qdrant.collection", schema.CollectionName);

        try
        {
            var existing = await client.ListCollectionsAsync();
            if (!existing.Any(c => c == schema.CollectionName))
            {
                // First registration: always use named VectorParamsMap so the alias migration
                // path can reliably detect and copy named vectors.
                await CreateNamedVectorCollectionAsync(schema.CollectionName, schema.Vectors);
                logger.LogInformation("Created Qdrant collection {Collection}", schema.CollectionName);
            }
            else
            {
                var info    = await client.GetCollectionInfoAsync(schema.CollectionName);
                var vecsCfg = info.Config.Params.VectorsConfig;

                // Detect dimension mismatches (breaking) and missing named vectors (migration).
                var missingVectors = new List<NamedVector>();

                foreach (var v in schema.Vectors)
                {
                    var (exists, registeredDim) = GetRegisteredDim(vecsCfg, v.Name);

                    if (exists && registeredDim != (ulong)v.Dimension)
                        throw new InvalidOperationException(
                            $"Qdrant collection '{schema.CollectionName}' vector '{v.Name}' has dimension " +
                            $"{registeredDim} but the schema declares {v.Dimension}. " +
                            $"Drop and re-register the schema to change dimensions.");

                    if (!exists)
                        missingVectors.Add(v);
                }

                if (missingVectors.Count > 0)
                {
                    logger.LogInformation(
                        "Qdrant collection {Collection} is missing vectors [{Vectors}] — migrating",
                        schema.CollectionName, string.Join(", ", missingVectors.Select(v => v.Name)));

                    await MigrateCollectionAsync(schema, vecsCfg);
                }
                else
                {
                    activity?.SetTag("qdrant.already_existed", true);
                    logger.LogInformation("Qdrant collection {Collection} up to date", schema.CollectionName);
                }
            }

            // Apply payload indexes on the logical name (alias resolves to physical after migration)
            await ApplyPayloadIndexesAsync(schema.CollectionName, schema.PayloadIndexes);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logger.LogError(ex, "ApplyCollectionAsync failed for {Collection}", schema.CollectionName);
            throw;
        }
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private static Value ToQdrantValue(object value) => value switch
    {
        string s           => s,
        bool b             => b,
        int i              => (long)i,
        long l             => l,
        float f            => (double)f,
        double d           => d,
        DateTime dt        => dt.ToString("o"),
        DateTimeOffset dto => dto.ToString("o"),
        _                  => value.ToString() ?? string.Empty
    };

    private async Task MigrateCollectionAsync(
        CollectionSchema schema,
        VectorsConfig? existingCfg)
    {
        var (isAlias, currentPhysical) = await ResolvePhysicalCollectionAsync(schema.CollectionName);
        var newPhysical = VersionedName(schema.CollectionName);

        // Build the full merged vector set: existing vectors + new vectors
        var allVectors = schema.Vectors.ToList();

        await CreateNamedVectorCollectionAsync(newPhysical, allVectors);

        // Copy all existing points; convert unnamed single-vector to named format if needed
        await CopyPointsAsync(currentPhysical, newPhysical, existingCfg, schema.Vectors);

        // Swap alias and drop old physical
        if (isAlias)
        {
            await client.DeleteAliasAsync(schema.CollectionName);
            await client.CreateAliasAsync(schema.CollectionName, newPhysical);
            await client.DeleteCollectionAsync(currentPhysical);
        }
        else
        {
            // First migration: convert from bare collection to versioned physical + alias
            await client.DeleteCollectionAsync(schema.CollectionName);
            await client.CreateAliasAsync(schema.CollectionName, newPhysical);
        }

        logger.LogInformation("Migrated Qdrant collection {Alias}: {Old} → {New}",
            schema.CollectionName, currentPhysical, newPhysical);
    }

    private async Task CopyPointsAsync(
        string source,
        string dest,
        VectorsConfig? sourceCfg,
        IReadOnlyList<NamedVector> schemaVectors)
    {
        PointId? nextOffset = null;
        var isSourceUnnamed = sourceCfg?.ConfigCase == VectorsConfig.ConfigOneofCase.Params;

        do
        {
            // ScrollAsync returns ScrollResponse; .Result is RepeatedField<RetrievedPoint>
            var scroll = await client.ScrollAsync(
                source,
                filter:          null,
                limit:           100u,
                offset:          nextOffset,
                payloadSelector: true,
                vectorsSelector: true);

            if (scroll.Result.Count == 0) break;

            var structs = scroll.Result.Select(p =>
            {
                var ps = new PointStruct { Id = p.Id };

                foreach (var kvp in p.Payload) ps.Payload[kvp.Key] = kvp.Value;

                // RetrievedPoint.Vectors is VectorsOutput — convert to Vectors for PointStruct
                ps.Vectors = ConvertVectors(
                    p.Vectors,
                    isSourceUnnamed,
                    schemaVectors.Count > 0
                        ? schemaVectors[0].Name
                        : null);

                return ps;
            }).ToList();

            await client.UpsertAsync(dest, structs);

            var offset = scroll.NextPageOffset;
            nextOffset = (offset is not null && (offset.HasNum || offset.HasUuid))
                ? offset
                : null;
        } while (nextOffset is not null);
    }

    // Converts VectorsOutput (from RetrievedPoint) to Vectors (for PointStruct).
    // When the source collection used a single unnamed vector and the destination uses named vectors,
    // wraps the unnamed vector data under the first schema vector name.
    private static Vectors? ConvertVectors(
        VectorsOutput? source,
        bool isSourceUnnamed,
        string? firstVectorName)
    {
        if (source is null || source.VectorsOptionsCase == VectorsOutput.VectorsOptionsOneofCase.None)
            return null;

        if (source.VectorsOptionsCase == VectorsOutput.VectorsOptionsOneofCase.Vector)
        {
            if (isSourceUnnamed && firstVectorName is not null)
            {
                // Unnamed single-vector → named for new collection layout
                var namedVecs = new NamedVectors();
                namedVecs.Vectors[firstVectorName] = new QdrantVector { Dense = source.Vector.Dense };
                return new Vectors { Vectors_ = namedVecs };
            }

            return new Vectors { Vector = new QdrantVector { Dense = source.Vector.Dense } };
        }

        // VectorsOutput.VectorsOptionsOneofCase.Vectors — named vectors
        var named = new NamedVectors();
        foreach (var kvp in source.Vectors.Vectors)
            named.Vectors[kvp.Key] = new QdrantVector { Dense = kvp.Value.Dense };
        return new Vectors { Vectors_ = named };
    }

    private async Task CreateNamedVectorCollectionAsync(string name, IReadOnlyList<NamedVector> vectors)
    {
        var paramsMap = new VectorParamsMap();
        foreach (var v in vectors)
            paramsMap.Map[v.Name] = new VectorParams { Size = (ulong)v.Dimension, Distance = Distance.Cosine };
        await client.CreateCollectionAsync(name, paramsMap);
    }

    private async Task ApplyPayloadIndexesAsync(string collectionName, IReadOnlyList<PayloadIndex> indexes)
    {
        foreach (var idx in indexes)
        {
            var schemaType = idx.Kind switch
            {
                PayloadIndexKind.Keyword  => PayloadSchemaType.Keyword,
                PayloadIndexKind.Integer  => PayloadSchemaType.Integer,
                PayloadIndexKind.Float    => PayloadSchemaType.Float,
                PayloadIndexKind.Boolean  => PayloadSchemaType.Bool,
                PayloadIndexKind.Datetime => PayloadSchemaType.Datetime,
                _                         => PayloadSchemaType.Keyword
            };
            await client.CreatePayloadIndexAsync(collectionName, idx.FieldName, schemaType);
        }
    }

    // Returns (vectorNameExists, registeredDimension).
    // Handles both unnamed single-vector (Params) and named-vector (ParamsMap) collection configs.
    private static (bool Exists, ulong Dim) GetRegisteredDim(VectorsConfig? cfg, string vectorName)
    {
        if (cfg is null) return (false, 0ul);

        return cfg.ConfigCase switch
        {
            VectorsConfig.ConfigOneofCase.Params =>
                // Unnamed single vector — matches the first (and only) named vector in the schema
                (true, cfg.Params.Size),
            VectorsConfig.ConfigOneofCase.ParamsMap =>
                cfg.ParamsMap.Map.TryGetValue(vectorName, out var vp)
                    ? (true, vp.Size)
                    : (false, 0ul),
            _ => (false, 0ul)
        };
    }

    // Resolves the logical collection name to its physical backing collection.
    // Returns (isAlias=false, logicalName) when the collection is not aliased.
    private async Task<(bool IsAlias, string Physical)> ResolvePhysicalCollectionAsync(string logicalName)
    {
        var aliases = await client.ListAliasesAsync();
        var match   = aliases.FirstOrDefault(a =>
            string.Equals(a.AliasName, logicalName, StringComparison.OrdinalIgnoreCase));
        return match is not null ? (true, match.CollectionName) : (false, logicalName);
    }

    private static string VersionedName(string logicalName) =>
        $"{logicalName}_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
}
