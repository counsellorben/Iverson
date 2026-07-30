using System.Globalization;
using System.Runtime.CompilerServices;
using Iverson.Api.Schema;

namespace Iverson.Api.Grpc;

/// <summary>
/// The decay-field convention for tensor re-ranking: which payload column carries a
/// document's recency timestamp, and the half-life curve that turns its stored value
/// into a decay weight in [0,1].
/// </summary>
internal static class DecayFieldResolver
{
    private const double HalfLifeDays = 180.0;

    // Keyed on schema IDENTITY via reference equality, not on derived content: SchemaRegistry
    // (Iverson.Api/Schema/SchemaRegistry.cs) stores one SchemaDescriptor instance per type in
    // its own ConcurrentDictionary and replaces the instance wholesale on RegisterAsync
    // (`_schemas[descriptor.TypeName] = descriptor`) — it is never rebuilt per request. So the
    // SchemaDescriptor handed to ResolveDecayField is stable across repeated calls for an
    // unchanged registration and becomes a brand-new object the moment RegisterSchema
    // re-registers the type, including when only a column's SqlType changes and the column
    // NAME set stays identical (a case a name-derived key could not distinguish). Reference
    // identity costs nothing to compute, so the metadata/column join only runs on a genuine
    // cache miss (a never-seen-before descriptor) — ObjectSearchGrpcService calls this once
    // per search request and must not pay for the join on every hit.
    //
    // ConditionalWeakTable.GetValue's createValueCallback can run more than once under a race
    // for the same key (only one result is kept), so the once-per-type ambiguity log below is
    // at-least-once, not exactly-once — same guarantee as a ConcurrentDictionary.GetOrAdd would
    // give, and acceptable for a registration-time diagnostic. Values are boxed in StrongBox
    // because ConditionalWeakTable cannot store a bare null, and null ("no decay field" /
    // "ambiguous") is a legitimate, cacheable answer.
    private static readonly ConditionalWeakTable<SchemaDescriptor, StrongBox<string?>> Cache = new();

    /// <summary>
    /// Resolves the camelCase payload key of the decay column for <paramref name="schema"/>,
    /// or null if there is no such column (zero candidates) or the choice is ambiguous
    /// (two or more candidates — this refuses to guess, and logs once per type).
    /// </summary>
    internal static string? ResolveDecayField(SchemaDescriptor schema, ILogger logger) =>
        Cache.GetValue(schema, s => new StrongBox<string?>(Resolve(s, logger))).Value;

    private static string? Resolve(SchemaDescriptor schema, ILogger logger)
    {
        var candidates = schema.ScalarColumns
            .Where(c => schema.MetadataColumns.Contains(c.Name))
            .Where(c => c.SqlType.Equals("TIMESTAMPTZ", StringComparison.OrdinalIgnoreCase) ||
                        c.SqlType.Equals("DATETIME", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0) return null;

        if (candidates.Count > 1)
        {
            logger.LogWarning(
                "[DecayField] type={Type} has {Count} candidate timestamp metadata columns " +
                "({Columns}); refusing to guess which one drives decay re-ranking.",
                schema.TypeName, candidates.Count, string.Join(", ", candidates.Select(c => c.Name)));
            return null;
        }

        return candidates[0].Name.ToCamelCase();
    }

    /// <summary>
    /// Turns a stored payload timestamp string into a decay value in [0,1] via
    /// 0.5 ^ (age / halfLife) with a fixed 180-day half-life. Returns null — signal
    /// absent, never a neutral 1.0 — when the value is null, empty, or unparseable.
    /// A future-dated timestamp (negative age — clock skew or bad ingestion data) is clamped
    /// to 1.0, maximum freshness, rather than exceeding the documented [0,1] range.
    /// </summary>
    internal static double? ComputeDecay(string? storedValue, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(storedValue)) return null;

        if (!DateTimeOffset.TryParse(
                storedValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp))
            return null;

        var ageDays = (now - timestamp).TotalDays;
        return Math.Min(1.0, Math.Pow(0.5, ageDays / HalfLifeDays));
    }
}
