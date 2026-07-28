using Iverson.Client.Contracts;

namespace Iverson.StarRocks;

/// <summary>
/// Registered in place of EngagementRepository when Engagement__Enabled is false.
/// Every method throws: the store is not deployed and never will be in this instance.
/// </summary>
internal sealed class DisabledEngagementStoreSearchService : IEngagementStoreSearchService
{
    private const string Message =
        "The engagement store is not deployed in this instance (Engagement__Enabled=false). " +
        "Search, aggregate, group-by and pipeline queries require StarRocks.";

    public Task<IEnumerable<dynamic>> SearchAsync(
        EngagementQuerySchema schema, SearchQuery? query, int page, int pageSize,
        IReadOnlyList<string>? fields = null, IReadOnlyList<JoinSpec>? joins = null,
        Func<string, EngagementQuerySchema?>? registry = null,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
        => throw new EngagementStoreDisabledException(Message);

    public Task<AggregationResult?> AggregateAsync(
        EngagementQuerySchema schema, SearchQuery? query, AggregationDescriptor spec,
        SearchQuery? having = null, IReadOnlyList<JoinSpec>? joins = null,
        Func<string, EngagementQuerySchema?>? registry = null,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
        => throw new EngagementStoreDisabledException(Message);

    public Task<IEnumerable<dynamic>> GroupByAsync(
        EngagementQuerySchema schema, GroupByRequest request,
        Func<string, EngagementQuerySchema?> registry,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
        => throw new EngagementStoreDisabledException(Message);

    public Task<IEnumerable<dynamic>> PipelineAsync(
        EngagementQuerySchema schema, PipelineRequest request,
        Func<string, EngagementQuerySchema?> registry,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
        => throw new EngagementStoreDisabledException(Message);
}
