using Qdrant.Client.Grpc;

namespace Iverson.Vector;

public interface IVectorService
{
    Task EnsureCollectionAsync(
        string collectionName,
        ulong vectorSize);
    Task ApplyCollectionAsync(CollectionSchema schema);
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
    Task DeleteAsync(string collectionName, ulong id);
}

public record VectorSearchResult(
    ulong Id,
    double Score,
    IReadOnlyDictionary<string, string> Payload);
