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

    public static EngagementQueryLimitOptions Default { get; } = new();
}
