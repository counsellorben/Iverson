using Elastic.Clients.Elasticsearch;

namespace Iverson.Elasticsearch;

public interface IElasticsearchService
{
    Task IndexDocumentAsync<T>(string indexName, string id, T document) where T : class;
    Task<T?> GetDocumentAsync<T>(string indexName, string id) where T : class;
    Task<IReadOnlyCollection<T>> SearchAsync<T>(string indexName, string query) where T : class;
    Task DeleteDocumentAsync(string indexName, string id);
    Task<bool> IndexExistsAsync(string indexName);
    Task CreateIndexAsync(string indexName);
    Task ApplyMappingAsync(IndexSchema schema);
    Task<IReadOnlyList<AggregationResult>> AggregateAsync(
        string indexName,
        string queryText,
        IReadOnlyList<AggregationDescriptor> specs);
}
