using System.Diagnostics;
using System.Globalization;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace Iverson.Vector;

public class IntelligenceVectorService(QdrantClient client) : IVectorQueryService, IVectorWriteService
{
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

    public async Task UpdateNamedVectorsAsync(
        string collectionName,
        ulong id,
        IReadOnlyDictionary<string, float[]> namedVectors)
    {
        using var activity = Telemetry.Source.StartActivity("qdrant.update_named_vectors", ActivityKind.Client);
        activity?.SetTag("db.system", "qdrant");
        activity?.SetTag("qdrant.collection", collectionName);
        activity?.SetTag("qdrant.point_id", id);
        activity?.SetTag("qdrant.vector_count", namedVectors.Count);

        var named = new NamedVectors();
        foreach (var (name, data) in namedVectors)
            named.Vectors[name] = data;

        var point = new PointVectors
        {
            Id      = id,
            Vectors = new Vectors { Vectors_ = named }
        };

        await client.UpdateVectorsAsync(collectionName, [point]);
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
            r.Payload.ToDictionary(kvp => kvp.Key, kvp => ToCanonicalString(kvp.Value))
        )).ToList();
    }

    public async Task<IReadOnlyList<VectorSearchResult>> SearchNamedAsync(
        string collectionName,
        string vectorName,
        float[] queryVector,
        ulong limit = 10,
        Filter? filter = null)
    {
        using var activity = Telemetry.Source.StartActivity("qdrant.search_named", ActivityKind.Client);
        activity?.SetTag("db.system", "qdrant");
        activity?.SetTag("qdrant.collection", collectionName);
        activity?.SetTag("qdrant.vector_name", vectorName);
        activity?.SetTag("qdrant.limit", limit);
        activity?.SetTag("qdrant.filtered", filter is not null);

        var results = await client.SearchAsync(
            collectionName,
            queryVector,
            filter:          filter,
            limit:           limit,
            payloadSelector: true,
            vectorName:      vectorName);

        activity?.SetTag("qdrant.result_count", results.Count);
        activity?.SetStatus(ActivityStatusCode.Ok);

        return results.Select(r => new VectorSearchResult(
            r.Id.Num,
            r.Score,
            r.Payload.ToDictionary(kvp => kvp.Key, kvp => ToCanonicalString(kvp.Value))
        )).ToList();
    }

    public async Task<IReadOnlyDictionary<ulong, float[]>> RetrieveNamedVectorAsync(
        string collectionName,
        IReadOnlyList<ulong> ids,
        string vectorName)
    {
        using var activity = Telemetry.Source.StartActivity("qdrant.retrieve_named_vector", ActivityKind.Client);
        activity?.SetTag("db.system", "qdrant");
        activity?.SetTag("qdrant.collection", collectionName);
        activity?.SetTag("qdrant.vector_name", vectorName);
        activity?.SetTag("qdrant.id_count", ids.Count);

        const int BatchSize = 512;   // 512 × 768 floats ≈ 1.6 MB, well under Grpc.Net.Client's 4 MB default

        var result = new Dictionary<ulong, float[]>();
        foreach (var batch in ids.Chunk(BatchSize))
        {
            var points = await client.RetrieveAsync(
                collectionName,
                batch.Select(id => (PointId)id).ToList(),
                payloadSelector: false,
                vectorSelector:  new[] { vectorName });

            foreach (var p in points)
            {
                if (p.Vectors?.Vectors?.Vectors.TryGetValue(vectorName, out var v) != true) continue;
                var data = v.Dense?.Data ?? v.Data;      // 1.18 exposes both; read whichever is set
                if (data is { Count: > 0 }) result[p.Id.Num] = data.ToArray();
            }
        }

        activity?.SetStatus(ActivityStatusCode.Ok);
        return result;
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

    public async Task DeleteByFilterAsync(string collectionName, Filter filter)
    {
        using var activity = Telemetry.Source.StartActivity("qdrant.delete_by_filter", ActivityKind.Client);
        activity?.SetTag("db.system", "qdrant");
        activity?.SetTag("qdrant.collection", collectionName);

        await client.DeleteAsync(collectionName, filter);
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    // InternalsVisibleTo access — lets the payload-kind classification be tested directly
    // without a live Qdrant connection.
    internal static string ToCanonicalString(Value v) => v.KindCase switch
    {
        Value.KindOneofCase.StringValue  => v.StringValue,
        Value.KindOneofCase.IntegerValue => v.IntegerValue.ToString(CultureInfo.InvariantCulture),
        Value.KindOneofCase.DoubleValue  => v.DoubleValue.ToString(CultureInfo.InvariantCulture),
        Value.KindOneofCase.BoolValue    => v.BoolValue ? "true" : "false",
        _                                => v.ToString()
    };

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
}
