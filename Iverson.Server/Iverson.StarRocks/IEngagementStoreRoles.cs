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

    /// <summary>
    /// The TYPE_ROWS source for MatchPattern (spec §3.2). Validates <paramref name="request"/> against
    /// <c>ColumnsFor</c> minus <see cref="MatchRowsRequest.ExcludedColumns"/> and streams the tenant's rows ordered by
    /// partition, order and key, each as an <c>OrdinalIgnoreCase</c> dictionary with SQL <c>NULL</c> as <c>null</c>.
    /// Reads at most <see cref="MatchRowsRequest.MaxRowsScanned"/> + 1 rows; the caller detects the overflow.
    /// A null or invalid tenant, an unprovisioned tenant or a missing table yields an empty sequence.
    /// </summary>
    IAsyncEnumerable<IDictionary<string, object?>> MatchRowsAsync(
        EngagementQuerySchema schema,
        MatchRowsRequest request,
        IReadOnlyDictionary<string, AuthorizationConstraint> authz,
        CancellationToken ct = default);
}
