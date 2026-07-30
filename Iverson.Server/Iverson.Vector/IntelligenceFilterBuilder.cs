using System.Globalization;
using Iverson.Client.Contracts;
using Qdrant.Client.Grpc;
using Range = Qdrant.Client.Grpc.Range;

namespace Iverson.Vector;

/// <summary>
/// Translates DSL <see cref="SearchClause"/> lists into a Qdrant <see cref="Filter"/>.
/// Used by SearchSimilar/SearchChunks — the SQL search paths reject VECTOR_SIMILAR and never
/// call this; this builder in turn rejects CONTAINS/STARTS_WITH/ENDS_WITH/VECTOR_SIMILAR since
/// Qdrant payload filtering has no equivalent of substring/prefix/suffix matching or nested
/// vector similarity.
/// </summary>
public static class IntelligenceFilterBuilder
{
    /// <param name="timestampColumns">
    /// camelCased property names whose payload values are stored as canonical round-trip ("o")
    /// timestamps. Operands on those properties are re-emitted in the same form so string
    /// equality matches. Omit (or pass null) to disable canonicalization entirely.
    /// </param>
    public static Filter Build(
        IReadOnlyList<SearchClause> clauses,
        SearchLogic logic,
        string rpcName,
        IReadOnlySet<string>? timestampColumns = null)
    {
        var filter = new Filter();

        foreach (var clause in clauses)
        {
            var (condition, negate) = BuildCondition(clause, rpcName, timestampColumns);
            var mustNot = negate ^ (clause.ClauseType == SearchClauseType.MustNot);

            if (mustNot && logic == SearchLogic.Or)
                // Under OR logic, a top-level MustNot would be ANDed against the whole filter
                // (Qdrant evaluates must_not conjunctively), silently turning "A OR NOT B" into
                // "A AND NOT B". Nest the negation inside Should instead so it participates in
                // the OR-group correctly.
                filter.Should.Add(!condition);
            else if (mustNot)
                filter.MustNot.Add(condition);
            else if (logic == SearchLogic.Or)
                filter.Should.Add(condition);
            else
                filter.Must.Add(condition);
        }

        return filter;
    }

    /// <summary>
    /// Builds a Filter matching points whose "parent_id" payload field equals the given key.
    /// Used for chunk-collection lookups/deletes, where every chunk point carries its owning
    /// entity's key under "parent_id" (see IntelligenceStoreConsumer).
    /// </summary>
    public static Filter MatchParentId(string parentKey)
    {
        var filter = new Filter();
        filter.Must.Add(Conditions.MatchKeyword("parent_id", parentKey));
        return filter;
    }

    /// <summary>
    /// Builds an EQUALS payload condition for a single property/value pair. Exposed for callers
    /// outside this assembly (SearchChunks metadata filtering) that translate individual clauses
    /// themselves rather than going through <see cref="Build"/>.
    /// </summary>
    public static Condition MatchEquality(
        string property,
        SearchValue value,
        IReadOnlySet<string>? timestampColumns = null) =>
        BuildEqualityCondition(property, value, timestampColumns);

    public static Filter? ApplyOwnership(Filter? filter, bool ownershipRequired, string? ownerFieldCamelCase, string? ownerValue)
    {
        if (!ownershipRequired) return filter;
        filter ??= new Filter();
        filter.Must.Add(Conditions.MatchKeyword(ownerFieldCamelCase!, ownerValue!));
        return filter;
    }

    private static (Condition Condition, bool Negate) BuildCondition(
        SearchClause clause, string rpcName, IReadOnlySet<string>? timestampColumns) =>
        clause.Operator switch
        {
            SearchOperator.Equals    => (BuildEqualityCondition(clause.Property, clause.Value, timestampColumns), false),
            SearchOperator.NotEquals => (BuildEqualityCondition(clause.Property, clause.Value, timestampColumns), true),
            SearchOperator.GreaterThan          => (Conditions.Range(clause.Property, new Range { Gt  = RequireNumber(clause) }), false),
            SearchOperator.LessThan             => (Conditions.Range(clause.Property, new Range { Lt  = RequireNumber(clause) }), false),
            SearchOperator.GreaterThanOrEquals  => (Conditions.Range(clause.Property, new Range { Gte = RequireNumber(clause) }), false),
            SearchOperator.LessThanOrEquals     => (Conditions.Range(clause.Property, new Range { Lte = RequireNumber(clause) }), false),
            SearchOperator.In => (Conditions.Match(
                clause.Property,
                RequireStringList(clause)
                    .Select(s => Canonicalize(clause.Property, s, timestampColumns))
                    .ToList()), false),
            _ => throw new FilterTranslationException(
                $"Operator '{clause.Operator}' is not supported by {rpcName} filters. Supported operators: " +
                "EQUALS, NOT_EQUALS, GREATER_THAN, LESS_THAN, GREATER_THAN_OR_EQUALS, LESS_THAN_OR_EQUALS, IN.")
        };

    private static double RequireNumber(SearchClause clause)
    {
        if (clause.Value.KindCase != SearchValue.KindOneofCase.NumberVal)
            throw new FilterTranslationException(
                $"{clause.Operator} filter on '{clause.Property}' requires a numeric value.");

        return clause.Value.NumberVal;
    }

    private static IReadOnlyList<string> RequireStringList(SearchClause clause)
    {
        if (clause.Value.KindCase != SearchValue.KindOneofCase.StringList)
            throw new FilterTranslationException(
                $"{clause.Operator} filter on '{clause.Property}' requires a string list value.");

        return clause.Value.StringList.Values;
    }

    private static Condition BuildEqualityCondition(
        string property, SearchValue value, IReadOnlySet<string>? timestampColumns) => value.KindCase switch
    {
        SearchValue.KindOneofCase.StringVal =>
            Conditions.MatchKeyword(property, Canonicalize(property, value.StringVal, timestampColumns)),
        SearchValue.KindOneofCase.BoolVal   => Conditions.Match(property, value.BoolVal),
        SearchValue.KindOneofCase.NumberVal => Conditions.Match(property, Convert.ToInt64(value.NumberVal)),
        _ => throw new FilterTranslationException(
            $"EQUALS/NOT_EQUALS filter on '{property}' requires a string, bool, or numeric value.")
    };

    /// <summary>
    /// Re-emits a filter operand on a timestamp property in the canonical round-trip ("o") form
    /// that IntelligenceStoreConsumer writes into the payload, so string comparison matches
    /// whatever format the caller sent. Non-timestamp properties, and operands that will not
    /// parse, pass through unchanged — a value that cannot be a timestamp was never going to
    /// match, and throwing here would turn a no-hit query into an error.
    /// </summary>
    private static string Canonicalize(string property, string value, IReadOnlySet<string>? timestampColumns) =>
        timestampColumns is not null
        && timestampColumns.Contains(property)
        && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto)
            ? dto.ToString("o", CultureInfo.InvariantCulture)
            : value;
}
