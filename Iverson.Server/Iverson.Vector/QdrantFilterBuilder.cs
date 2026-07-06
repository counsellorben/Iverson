using Grpc.Core;
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
public static class QdrantFilterBuilder
{
    public static Filter Build(IReadOnlyList<SearchClause> clauses, SearchLogic logic, string rpcName)
    {
        var filter = new Filter();

        foreach (var clause in clauses)
        {
            var (condition, negate) = BuildCondition(clause, rpcName);
            var mustNot = negate ^ (clause.ClauseType == SearchClauseType.MustNot);

            if (mustNot)
                filter.MustNot.Add(condition);
            else if (logic == SearchLogic.Or)
                filter.Should.Add(condition);
            else
                filter.Must.Add(condition);
        }

        return filter;
    }

    private static (Condition Condition, bool Negate) BuildCondition(SearchClause clause, string rpcName) =>
        clause.Operator switch
        {
            SearchOperator.Equals    => (BuildEqualityCondition(clause.Property, clause.Value), false),
            SearchOperator.NotEquals => (BuildEqualityCondition(clause.Property, clause.Value), true),
            SearchOperator.GreaterThan          => (Conditions.Range(clause.Property, new Range { Gt  = clause.Value.NumberVal }), false),
            SearchOperator.LessThan             => (Conditions.Range(clause.Property, new Range { Lt  = clause.Value.NumberVal }), false),
            SearchOperator.GreaterThanOrEquals  => (Conditions.Range(clause.Property, new Range { Gte = clause.Value.NumberVal }), false),
            SearchOperator.LessThanOrEquals     => (Conditions.Range(clause.Property, new Range { Lte = clause.Value.NumberVal }), false),
            SearchOperator.In => (Conditions.Match(clause.Property, clause.Value.StringList.Values.ToList()), false),
            _ => throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Operator '{clause.Operator}' is not supported by {rpcName} filters. Supported operators: " +
                "EQUALS, NOT_EQUALS, GREATER_THAN, LESS_THAN, GREATER_THAN_OR_EQUALS, LESS_THAN_OR_EQUALS, IN."))
        };

    private static Condition BuildEqualityCondition(string property, SearchValue value) => value.KindCase switch
    {
        SearchValue.KindOneofCase.StringVal => Conditions.MatchKeyword(property, value.StringVal),
        SearchValue.KindOneofCase.BoolVal   => Conditions.Match(property, value.BoolVal),
        SearchValue.KindOneofCase.NumberVal => Conditions.Match(property, Convert.ToInt64(value.NumberVal)),
        _ => throw new RpcException(new Status(StatusCode.InvalidArgument,
            $"EQUALS/NOT_EQUALS filter on '{property}' requires a string, bool, or numeric value."))
    };
}
