using System.Linq.Expressions;
using Iverson.Client.Contracts;

namespace Iverson.Client.Search;

/// <summary>
/// Fluent builder for <see cref="SearchSimilarRequest"/> (Qdrant vector similarity search).
/// Filters accept EQUALS, NOT_EQUALS, GREATER_THAN, LESS_THAN, GREATER_THAN_OR_EQUALS,
/// LESS_THAN_OR_EQUALS, and IN — CONTAINS/STARTS_WITH/ENDS_WITH/VECTOR_SIMILAR have no Qdrant
/// payload-filter equivalent and are rejected at the point of the <see cref="Where{TValue}"/> call.
/// </summary>
public sealed class QuerySimilarBuilder<T> where T : class
{
    private readonly string _typeName;
    private readonly string _property;
    private string _query = string.Empty;
    private uint _topK = 10;
    private SearchLogic _logic = SearchLogic.And;
    private readonly List<SearchClause> _filters = [];

    internal QuerySimilarBuilder(string typeName, string property)
    {
        _typeName = typeName;
        _property = property;
    }

    public QuerySimilarBuilder<T> Text(string query) { _query = query; return this; }
    public QuerySimilarBuilder<T> TopK(uint topK) { _topK = topK; return this; }
    public QuerySimilarBuilder<T> WithLogic(SearchLogic logic) { _logic = logic; return this; }

    public QuerySimilarBuilder<T> Where<TValue>(
        Expression<Func<T, TValue>> property, SearchOperator op, TValue value)
    {
        if (op is SearchOperator.Contains or SearchOperator.StartsWith
                or SearchOperator.EndsWith or SearchOperator.VectorSimilar)
            throw new InvalidOperationException(
                $"Operator '{op}' is not supported by SearchSimilar filters. Supported operators: " +
                "Equals, NotEquals, GreaterThan, LessThan, GreaterThanOrEquals, LessThanOrEquals, In.");

        _filters.Add(new SearchClause
        {
            Property   = PropertyNameExtractor.PropertyName(property),
            Operator   = op,
            Value      = SearchValueConverter.ToSearchValue(value),
            ClauseType = SearchClauseType.Filter
        });
        return this;
    }

    public SearchSimilarRequest Build(string? traceId = null)
    {
        var request = new SearchSimilarRequest
        {
            TypeName    = _typeName,
            Property    = _property,
            Query       = _query,
            TopK        = _topK,
            FilterLogic = _logic,
            TraceId     = traceId ?? string.Empty
        };
        request.Filter.AddRange(_filters);
        return request;
    }
}
