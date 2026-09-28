using Qdrant.Client.Grpc;

namespace Iverson.Vector;

public interface IVectorQueryService
{
    Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string collectionName,
        float[] queryVector,
        ulong limit = 10);
    Task<IReadOnlyList<VectorSearchResult>> SearchNamedAsync(
        string collectionName,
        string vectorName,
        float[] queryVector,
        ulong limit = 10,
        Filter? filter = null);
    Task<IReadOnlyDictionary<ulong, float[]>> RetrieveNamedVectorAsync(
        string collectionName,
        IReadOnlyList<ulong> ids,
        string vectorName,
        CancellationToken ct = default);

    /// <summary>Approximate point count of a collection (Qdrant collection info).</summary>
    Task<ulong> GetPointCountAsync(string collectionName);

    /// <summary>
    /// Payload of each listed point, canonicalised to strings exactly as SearchNamedAsync does.
    /// Ids with no point are absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<ulong, IReadOnlyDictionary<string, string>>> RetrievePayloadAsync(
        string collectionName, IReadOnlyList<ulong> ids);

    /// <summary>
    /// One page of the points matching <paramref name="filter"/>, returning only <paramref name="payloadFields"/>
    /// and, when <paramref name="vectorName"/> is set, that named vector. Pass the previous page's
    /// <see cref="VectorScrollPage.NextOffset"/> as <paramref name="offset"/>; null starts at the beginning.
    /// </summary>
    Task<VectorScrollPage> ScrollAsync(
        string collectionName,
        Filter? filter,
        IReadOnlyList<string> payloadFields,
        string? vectorName,
        uint pageSize,
        PointId? offset = null,
        CancellationToken ct = default);
}

public interface IVectorSchemaManager
{
    Task EnsureCollectionAsync(string collectionName, ulong vectorSize);
    Task ApplyCollectionAsync(CollectionSchema schema);

    /// <summary>
    /// A non-mutating Qdrant connectivity check — lists collections rather than creating one.
    /// CSR finding #7: the anonymous <c>/health</c> endpoint used to call
    /// <see cref="EnsureCollectionAsync"/> against a fixed probe collection name, which CREATES
    /// the collection if it does not already exist — a write, reachable with no authentication.
    /// </summary>
    Task<bool> PingAsync();
}

public interface IVectorWriteService
{
    Task UpsertAsync(
        string collectionName,
        ulong id,
        float[] vector,
        IReadOnlyDictionary<string, object>? payload = null);
    Task UpsertNamedAsync(
        string collectionName,
        ulong id,
        IReadOnlyDictionary<string, float[]> namedVectors,
        IReadOnlyDictionary<string, object>? payload = null);
    Task UpdateNamedVectorsAsync(
        string collectionName,
        ulong id,
        IReadOnlyDictionary<string, float[]> namedVectors);
    Task SetPayloadAsync(string collectionName, ulong id, IReadOnlyDictionary<string, object> payload);
    Task DeleteAsync(string collectionName, ulong id);
    Task DeleteByFilterAsync(string collectionName, Filter filter);
}

public record VectorSearchResult(
    ulong Id,
    double Score,
    IReadOnlyDictionary<string, string> Payload);
