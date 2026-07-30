using System.Collections.Concurrent;
using System.Globalization;
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

    // Registration-time diagnostic, not a per-request counter: GetOrAdd's value factory may
    // run more than once under a racing first call for the same cache key, so the
    // ambiguous-type log line below is at-least-once, not exactly-once. Also keeps the
    // metadata/column join off the hot path — ObjectSearchGrpcService serves concurrent
    // gRPC requests, so this must be a thread-safe collection rather than a plain Dictionary.
    //
    // Keyed on schema IDENTITY, not just TypeName: SchemaDescriptor has no version/etag field,
    // and RegisterSchema is a live RPC that can re-register an existing type with a different
    // set of timestamp metadata columns. The key folds in the resolved candidate column names
    // (see BuildCacheKey) so a re-registration that changes the candidate set naturally misses
    // the old cache entry instead of serving a stale field forever.
    private static readonly ConcurrentDictionary<string, string?> Cache = new();

    /// <summary>
    /// Resolves the camelCase payload key of the decay column for <paramref name="schema"/>,
    /// or null if there is no such column (zero candidates) or the choice is ambiguous
    /// (two or more candidates — this refuses to guess, and logs once per type).
    /// </summary>
    internal static string? ResolveDecayField(SchemaDescriptor schema, ILogger logger) =>
        Cache.GetOrAdd(BuildCacheKey(schema), _ => Resolve(schema, logger));

    // Schema identity = type name + the sorted set of candidate timestamp-metadata column
    // names. Any change to which columns are timestamp-typed AND declared metadata (add,
    // remove, rename, or retype) changes this key, so ResolveDecayField naturally re-resolves
    // instead of reading a stale cache entry. Column names, not SqlType, are enough here: a
    // retype into/out of TIMESTAMPTZ/DATETIME changes set membership just the same as a rename.
    private static string BuildCacheKey(SchemaDescriptor schema)
    {
        var candidateNames = schema.ScalarColumns
            .Where(c => schema.MetadataColumns.Contains(c.Name))
            .Where(c => c.SqlType.Equals("TIMESTAMPTZ", StringComparison.OrdinalIgnoreCase) ||
                        c.SqlType.Equals("DATETIME", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

        return $"{schema.TypeName}|{string.Join(',', candidateNames)}";
    }

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
