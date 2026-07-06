using System.Linq.Expressions;
using Iverson.Client.Contracts;

namespace Iverson.Client.Search;

/// <summary>
/// Fluent builder for <see cref="SearchChunksRequest"/> (Qdrant chunk/RAG search).
/// Supports at most one filter clause: an EQUALS match on the entity's primary-key property —
/// the chunks collection's payload only indexes <c>parent_id</c>, so richer filtering isn't
/// available (mirrors the server-side restriction in <c>ObjectSearchGrpcService.SearchChunks</c>).
/// The "at most one filter" rule is enforced at <see cref="Build"/> time rather than eagerly
/// in <see cref="Where{TValue}"/>, so that a second <c>Where</c> call in a fluent chain fails
/// only when the request is materialized.
/// </summary>
public sealed class QueryChunksBuilder<T> where T : class
{
    private readonly string _typeName;
    private readonly string _property;
    private string _query = string.Empty;
    private uint _topK = 10;
    private readonly List<SearchClause> _filters = [];

    internal QueryChunksBuilder(string typeName, string property)
    {
        _typeName = typeName;
        _property = property;
    }

    public QueryChunksBuilder<T> Text(string query) { _query = query; return this; }
    public QueryChunksBuilder<T> TopK(uint topK) { _topK = topK; return this; }

    public QueryChunksBuilder<T> Where<TValue>(
        Expression<Func<T, TValue>> property, SearchOperator op, TValue value)
    {
        if (op != SearchOperator.Equals)
            throw new InvalidOperationException(
                $"SearchChunks only supports an Equals filter on the primary-key property; got '{op}'.");

        _filters.Add(new SearchClause
        {
            Property   = PropertyName(property),
            Operator   = op,
            Value      = SearchValueConverter.ToSearchValue(value),
            ClauseType = SearchClauseType.Filter
        });
        return this;
    }

    public SearchChunksRequest Build(string? traceId = null)
    {
        if (_filters.Count > 1)
            throw new InvalidOperationException("SearchChunks supports at most one filter clause.");

        var request = new SearchChunksRequest
        {
            TypeName = _typeName,
            Property = _property,
            Query    = _query,
            TopK     = _topK,
            TraceId  = traceId ?? string.Empty
        };
        request.Filter.AddRange(_filters);
        return request;
    }

    private static string PropertyName<TValue>(Expression<Func<T, TValue>> expr) =>
        expr.Body is MemberExpression member
            ? member.Member.Name
            : throw new ArgumentException("Expression must be a direct property access, e.g. x => x.Body");
}
