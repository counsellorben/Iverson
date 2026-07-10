using Iverson.Client.Contracts;

namespace Iverson.StarRocks;

public interface IEngagementStoreQueryExecutor
{
    Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null);
    Task<int> ExecuteAsync(string sql, object? param = null);
}

public interface IEngagementStoreSchemaManager
{
    Task ApplyTableAsync(StarRocksTableSchema schema);
}

public interface IEngagementStoreHealthCheck
{
    Task<StarRocksHealthStatus> CheckHealthAsync();
    Task<bool> IsHealthyAsync();
}

public interface IEngagementStoreEntityStore
{
    Task UpsertAsync(StarRocksTableSchema schema, string payloadJson);
    Task DeleteAsync(string tableName, string keyColumn, string keyValue);
}

public interface IEngagementStoreSearchService
{
    Task<IEnumerable<dynamic>> SearchAsync(
        StarRocksQuerySchema schema,
        SearchQuery? query,
        int page,
        int pageSize,
        IReadOnlyList<string>? fields = null,
        IReadOnlyList<JoinSpec>? joins = null,
        Func<string, StarRocksQuerySchema?>? registry = null);

    Task<AggregationResult?> AggregateAsync(
        StarRocksQuerySchema schema,
        SearchQuery? query,
        AggregationDescriptor spec,
        SearchQuery? having = null,
        IReadOnlyList<JoinSpec>? joins = null,
        Func<string, StarRocksQuerySchema?>? registry = null);

    Task<IEnumerable<dynamic>> GroupByAsync(
        StarRocksQuerySchema schema,
        GroupByRequest request,
        Func<string, StarRocksQuerySchema?> registry);

    Task<IEnumerable<dynamic>> PipelineAsync(
        StarRocksQuerySchema schema,
        PipelineRequest request,
        Func<string, StarRocksQuerySchema?> registry);
}
