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
        string vectorName);

    /// <summary>Approximate point count of a collection (Qdrant collection info).</summary>
    Task<ulong> GetPointCountAsync(string collectionName);

    /// <summary>
    /// Payload of each listed point, canonicalised to strings exactly as SearchNamedAsync does.
    /// Ids with no point are absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<ulong, IReadOnlyDictionary<string, string>>> RetrievePayloadAsync(
        string collectionName, IReadOnlyList<ulong> ids);
}

public interface IVectorSchemaManager
{
    Task EnsureCollectionAsync(string collectionName, ulong vectorSize);
    Task ApplyCollectionAsync(CollectionSchema schema);
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
    Task DeleteAsync(string collectionName, ulong id);
    Task DeleteByFilterAsync(string collectionName, Filter filter);
}

public record VectorSearchResult(
    ulong Id,
    double Score,
    IReadOnlyDictionary<string, string> Payload);
