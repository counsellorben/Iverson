namespace Iverson.StarRocks;

/// <summary>
/// Shared cap-checking for <see cref="StarRocksQueryBuilder"/> and
/// <see cref="StarRocksPipelineBuilder"/> (CSR finding #5) — one place so the two builders cannot
/// disagree on the rejection message shape, and so a limit change only has one call site to keep
/// in sync with <see cref="EngagementQueryLimitOptions"/>.
/// </summary>
internal static class EngagementQueryLimitValidator
{
    internal static void CheckClauseCount(int count, EngagementQueryLimitOptions limits, string clauseKind)
    {
        if (count > limits.MaxClauses)
            throw new EngagementQueryTranslationException(
                $"Query has {count} {clauseKind} clauses, exceeding the maximum of {limits.MaxClauses}.");
    }

    internal static void CheckJoinCount(int count, EngagementQueryLimitOptions limits)
    {
        if (count > limits.MaxJoins)
            throw new EngagementQueryTranslationException(
                $"Query has {count} joins, exceeding the maximum of {limits.MaxJoins}.");
    }

    internal static void CheckGroupByKeyCount(int count, EngagementQueryLimitOptions limits)
    {
        if (count > limits.MaxGroupByKeys)
            throw new EngagementQueryTranslationException(
                $"Query has {count} GROUP BY keys, exceeding the maximum of {limits.MaxGroupByKeys}.");
    }

    internal static void CheckPipelineStepCount(int count, EngagementQueryLimitOptions limits)
    {
        if (count > limits.MaxPipelineSteps)
            throw new EngagementQueryTranslationException(
                $"Pipeline has {count} steps, exceeding the maximum of {limits.MaxPipelineSteps}.");
    }

    internal static void CheckWindowFunctionCount(int count, EngagementQueryLimitOptions limits)
    {
        if (count > limits.MaxWindowFunctions)
            throw new EngagementQueryTranslationException(
                $"Pipeline has {count} window functions, exceeding the maximum of {limits.MaxWindowFunctions}.");
    }

    // ── CSR finding #6: query-DSL OUTPUT caps (result-set size), alongside the SHAPE caps
    // above — same file, same exception shape, same configuration mechanism.

    internal static void CheckPageSize(int resolvedLimit, EngagementQueryLimitOptions limits)
    {
        if (resolvedLimit > limits.MaxPageSize)
            throw new EngagementQueryTranslationException(
                $"Requested page size {resolvedLimit} exceeds the maximum of {limits.MaxPageSize}.");
    }

    internal static void CheckAggregationSize(int resolvedSize, EngagementQueryLimitOptions limits)
    {
        if (resolvedSize > limits.MaxAggregationSize)
            throw new EngagementQueryTranslationException(
                $"Requested aggregation size {resolvedSize} exceeds the maximum of {limits.MaxAggregationSize}.");
    }

    internal static void CheckGroupByLimit(int resolvedLimit, EngagementQueryLimitOptions limits)
    {
        if (resolvedLimit > limits.MaxGroupByLimit)
            throw new EngagementQueryTranslationException(
                $"Requested limit {resolvedLimit} exceeds the maximum of {limits.MaxGroupByLimit}.");
    }
}
