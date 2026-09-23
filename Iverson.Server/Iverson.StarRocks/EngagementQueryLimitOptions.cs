namespace Iverson.StarRocks;

/// <summary>
/// Caps on the SHAPE of a query DSL request (CSR finding #5): with no limit on clause count, join
/// count, GROUP BY key count, pipeline step count, or window function count, an authenticated
/// tenant user can compose a single Search/Aggregate/GroupBy/Pipeline request expensive enough to
/// degrade StarRocks for every tenant on the cluster — not just their own.
///
/// <para>These are request-SHAPE caps (how many clauses/joins/etc. a request declares), not
/// row-count or execution-time limits. A StarRocks-side query timeout / memory limit is the
/// deployment-side complement to this — that belongs in the Helm chart, not this code.</para>
///
/// <para>Bound the same way as <see cref="EngagementResilienceOptions"/>: constructed by the
/// caller (see <c>Program.cs</c>'s <c>AddStarRocks</c> call) from individual
/// <c>IConfiguration.GetValue</c> reads under the <see cref="Section"/> key, rather than the
/// standard <c>IOptions&lt;T&gt;</c> binding pattern this codebase does not use for
/// <see cref="EngagementResilienceOptions"/> either.</para>
/// </summary>
public sealed class EngagementQueryLimitOptions
{
    public const string Section = "StarRocks:QueryLimits";

    /// <summary>Max WHERE or HAVING clauses in a single clause list. Checked independently per
    /// list a request carries (e.g. BuildAggregate's WHERE and HAVING are each checked against
    /// this on their own, not summed) and per pipeline step's own <c>where</c>/<c>having</c>.
    /// 50 is generous for any legitimate filter while still bounding the WHERE/HAVING string's
    /// own construction cost and the number of bound parameters.</summary>
    public int MaxClauses { get; init; } = 50;

    /// <summary>Max JOINs in a single request — BuildSearch/BuildGroupBy's <c>joins</c>, or a
    /// pipeline step's <c>joins</c>, checked independently per step. Each join StarRocks plans is
    /// combinatorially more expensive than the last; 10 already covers any realistic multi-entity
    /// query this DSL supports.</summary>
    public int MaxJoins { get; init; } = 10;

    /// <summary>Max GROUP BY keys — BuildGroupBy's <c>keys</c>, BuildAggregate's Terms
    /// <c>GroupByFields</c>, or a pipeline step's <c>group_by</c>, checked independently per step.
    /// Grouping cardinality grows with the PRODUCT of key cardinalities, so this is deliberately
    /// tighter than <see cref="MaxClauses"/>; 10 keys is already far past what a legible aggregate
    /// result set needs.</summary>
    public int MaxGroupByKeys { get; init; } = 10;

    /// <summary>Max steps in one pipeline request — each step compiles to another chained CTE,
    /// and StarRocks must materialize/plan every one.</summary>
    public int MaxPipelineSteps { get; init; } = 20;

    /// <summary>Max window functions, summed across every step in one pipeline request — each is
    /// its own sort/partition pass over its step's input.</summary>
    public int MaxWindowFunctions { get; init; } = 10;

    /// <summary>CSR finding #6: max rows a single page of <c>Search</c> may request — the
    /// resolved LIMIT after defaulting (<c>pageSize &gt; 0 ? pageSize : 50</c>). Unlike the shape
    /// caps above (which bound how EXPENSIVE a request's SQL is to plan), this bounds how much the
    /// StarRocks read + Dapper materialization + gRPC response can produce for one page — what an
    /// authenticated tenant user could otherwise inflate to OOM an api replica. 1000 is
    /// comfortably above any legitimate UI page size while still bounding per-request memory.</summary>
    public int MaxPageSize { get; init; } = 1000;

    /// <summary>CSR finding #6: max bucket count for a Terms aggregation's resolved
    /// <c>spec.Size</c> (<c>spec.Size &gt; 0 ? spec.Size : 10</c>) — the number of GROUP BY
    /// buckets StarRocks returns and the API materializes into an <c>AggregationResult</c>. Same
    /// order of magnitude as <see cref="MaxPageSize"/>, for the same reason: this is an
    /// output-size cap, not a shape cap.</summary>
    public int MaxAggregationSize { get; init; } = 1000;

    /// <summary>CSR finding #6: max resolved LIMIT for <c>GroupByRequest.Limit</c> (used by
    /// <c>StarRocksQueryBuilder.BuildGroupBy</c>) and <c>PipelineRequest.Limit</c> (used by
    /// <c>StarRocksPipelineBuilder.Build</c>) — both default to 10,000 when unset and are
    /// otherwise unbounded upward today. Set to that same existing default so a request that
    /// omits Limit (and so gets the implicit 10,000) is unaffected; only a request that
    /// explicitly asks for more is capped.</summary>
    public int MaxGroupByLimit { get; init; } = 10_000;

    /// <summary>CSR finding #6: max <c>top_k</c> across the three vector-search call sites in
    /// <c>ObjectSearchGrpcService</c> (SearchSimilar, its via-chunks routing, and SearchChunks).
    /// Each fetches up to <c>OverFetchFactor</c> (4) times top_k candidates from Qdrant before
    /// re-ranking/diversifying back down to top_k, so an uncapped top_k is a 4x-amplified version
    /// of the same OOM/availability risk as an uncapped page size. 1000 matches
    /// <see cref="MaxPageSize"/> and the top_k=1000 example already used in
    /// ObjectSearchGrpcService's own OverFetchFactor doc comment.</summary>
    public int MaxTopK { get; init; } = 1000;

    /// <summary>CSR finding #6: max relation-traversal depth for <c>MappingGetRequest.Depth</c>
    /// (<c>ObjectMappingGrpcService.Get</c> → <c>EntityRelationResolver.ResolveRelationsAsync</c>),
    /// currently unbounded. Each level can fan out across every relation on the type, so depth is
    /// the dominant term in the resolver's worst-case cost; 5 is generous for any legitimate
    /// object-graph read while keeping that fan-out bounded. <see cref="EntityRelationResolver"/>
    /// (in Iverson.Api) also carries an independent visited-set cycle guard as a correctness
    /// backstop — this cap is the primary defense, not a substitute for it.</summary>
    public int MaxRelationDepth { get; init; } = 5;

    public static EngagementQueryLimitOptions Default { get; } = new();
}
