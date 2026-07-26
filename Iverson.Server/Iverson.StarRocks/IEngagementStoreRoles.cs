using Iverson.Client.Contracts;

namespace Iverson.StarRocks;

public interface IEngagementStoreQueryExecutor
{
    Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null);
    Task<int> ExecuteAsync(string sql, object? param = null);
}

public interface IEngagementStoreHealthCheck
{
    Task<EngagementHealthStatus> CheckHealthAsync();
    Task<bool> IsHealthyAsync();
}

public interface IEngagementStoreEntityStore
{
    Task UpsertAsync(EngagementTableSchema schema, string payloadJson, string tenantId);
    Task DeleteAsync(string tableName, string keyColumn, string keyValue, string tenantId);
    Task EnsureTenantProvisionedAsync(string tenantId, EngagementTableSchema schema);
}

public interface IEngagementStoreSearchService
{
    Task<IEnumerable<dynamic>> SearchAsync(
        EngagementQuerySchema schema,
        SearchQuery? query,
        int page,
        int pageSize,
        IReadOnlyList<string>? fields = null,
        IReadOnlyList<JoinSpec>? joins = null,
        Func<string, EngagementQuerySchema?>? registry = null,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null);

    Task<AggregationResult?> AggregateAsync(
        EngagementQuerySchema schema,
        SearchQuery? query,
        AggregationDescriptor spec,
        SearchQuery? having = null,
        IReadOnlyList<JoinSpec>? joins = null,
        Func<string, EngagementQuerySchema?>? registry = null,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null);

    Task<IEnumerable<dynamic>> GroupByAsync(
        EngagementQuerySchema schema,
        GroupByRequest request,
        Func<string, EngagementQuerySchema?> registry,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null);

    Task<IEnumerable<dynamic>> PipelineAsync(
        EngagementQuerySchema schema,
        PipelineRequest request,
        Func<string, EngagementQuerySchema?> registry,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null);
}
