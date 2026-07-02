using System.Linq.Expressions;
using Iverson.Client.Contracts;

namespace Iverson.Client.Search;

/// <summary>
/// Fluent DSL builder that compiles to a <see cref="SearchRequest"/> or <see cref="AggregateRequest"/>.
/// </summary>
public sealed class QueryBuilder<T> where T : class
{
    private readonly string                _typeName;
    private readonly List<SearchClause>    _clauses      = [];
    private readonly List<SearchSort>      _sorts        = [];
    private readonly List<AggregationSpec> _aggregations = [];
    private readonly List<JoinSpec>        _joins        = [];
    private SearchLogic _logic    = SearchLogic.And;
    private int         _page     = 1;
    private int         _pageSize = 20;

    internal QueryBuilder(string typeName) => _typeName = typeName;

    // ── Clause builders ────────────────────────────────────────────────────────

    /// <summary>Adds a FILTER clause (applied before scoring).</summary>
    public QueryBuilder<T> Where<TValue>(
        Expression<Func<T, TValue>> property,
        SearchOperator op,
        TValue value)
        => AddClause(property, op, value, SearchClauseType.Filter);

    /// <summary>Adds a MUST clause (required for a match).</summary>
    public QueryBuilder<T> And<TValue>(
        Expression<Func<T, TValue>> property,
        SearchOperator op,
        TValue value)
        => AddClause(property, op, value, SearchClauseType.Must);

    /// <summary>Adds a SHOULD clause (boosts score but not required).</summary>
    public QueryBuilder<T> Or<TValue>(
        Expression<Func<T, TValue>> property,
        SearchOperator op,
        TValue value)
        => AddClause(property, op, value, SearchClauseType.Should);

    /// <summary>Adds a MUST_NOT clause (excludes matches).</summary>
    public QueryBuilder<T> Not<TValue>(
        Expression<Func<T, TValue>> property,
        SearchOperator op,
        TValue value)
        => AddClause(property, op, value, SearchClauseType.MustNot);

    // ── Vector similarity ──────────────────────────────────────────────────────
    // Dedicated overloads decouple the property type (string) from the query type
    // (float[]) — needed when [IversonEmbedding] is placed on a string property.

    public QueryBuilder<T> WhereVectorSimilar<TValue>(
        Expression<Func<T, TValue>> property, float[] queryVector)
        => AddVectorClause(property, queryVector, SearchClauseType.Filter);

    public QueryBuilder<T> AndVectorSimilar<TValue>(
        Expression<Func<T, TValue>> property, float[] queryVector)
        => AddVectorClause(property, queryVector, SearchClauseType.Must);

    public QueryBuilder<T> OrVectorSimilar<TValue>(
        Expression<Func<T, TValue>> property, float[] queryVector)
        => AddVectorClause(property, queryVector, SearchClauseType.Should);

    public QueryBuilder<T> NotVectorSimilar<TValue>(
        Expression<Func<T, TValue>> property, float[] queryVector)
        => AddVectorClause(property, queryVector, SearchClauseType.MustNot);

    // ── Sorting and paging ─────────────────────────────────────────────────────

    public QueryBuilder<T> OrderBy<TValue>(
        Expression<Func<T, TValue>> property,
        bool descending = false)
    {
        _sorts.Add(new SearchSort { Property = PropertyName(property), Descending = descending });
        return this;
    }

    public QueryBuilder<T> Page(int page, int size = 20)
    {
        _page     = page;
        _pageSize = size;
        return this;
    }

    /// <summary>
    /// Controls how top-level clauses are combined when the server builds its query.
    /// Defaults to AND.
    /// </summary>
    public QueryBuilder<T> WithLogic(SearchLogic logic)
    {
        _logic = logic;
        return this;
    }

    // ── Joins ───────────────────────────────────────────────────────────────────

    /// <summary>Joins another registered type via matching fields.</summary>
    public QueryBuilder<T> Join<TRight>(
        Expression<Func<T, object>> leftField,
        Expression<Func<TRight, object>> rightField,
        JoinKind kind = JoinKind.Inner)
        where TRight : class
    {
        _joins.Add(new JoinSpec
        {
            LeftType   = typeof(T).Name,
            RightType  = typeof(TRight).Name,
            LeftField  = PropertyNameObj(leftField),
            RightField = PropertyNameObj(rightField),
            Kind       = kind
        });
        return this;
    }

    // ── Aggregations ───────────────────────────────────────────────────────────

    /// <summary>Bucket documents by distinct values of a field.</summary>
    public QueryBuilder<T> GroupBy<TValue>(Expression<Func<T, TValue>> property, int size = 10)
    {
        var field = PropertyName(property);
        _aggregations.Add(new AggregationSpec { Name = $"{field}_terms", Type = AggregationType.Terms, Field = field, Size = size });
        return this;
    }

    /// <summary>Bucket documents by calendar interval on a date field.</summary>
    public QueryBuilder<T> ByDateInterval<TValue>(
        Expression<Func<T, TValue>> property,
        string calendarInterval,
        string? timeZone = null)
    {
        var field = PropertyName(property);
        var spec  = new AggregationSpec
        {
            Name             = $"{field}_date_histogram",
            Type             = AggregationType.DateHistogram,
            Field            = field,
            CalendarInterval = calendarInterval
        };
        if (timeZone is not null) spec.TimeZone = timeZone;
        _aggregations.Add(spec);
        return this;
    }

    /// <summary>Bucket documents into explicit numeric ranges.</summary>
    public QueryBuilder<T> ByRange<TValue>(
        Expression<Func<T, TValue>> property,
        params (string Key, double? From, double? To)[] buckets)
    {
        var field = PropertyName(property);
        var spec  = new AggregationSpec { Name = $"{field}_range", Type = AggregationType.Range, Field = field };
        foreach (var (key, from, to) in buckets)
            spec.RangeBuckets.Add(new RangeBucket { Key = key, From = from, To = to });
        _aggregations.Add(spec);
        return this;
    }

    /// <summary>Compute the average of a numeric field.</summary>
    public QueryBuilder<T> Avg<TValue>(Expression<Func<T, TValue>> property)
        => AddMetricAgg(property, AggregationType.Avg);

    /// <summary>Compute the sum of a numeric field.</summary>
    public QueryBuilder<T> Sum<TValue>(Expression<Func<T, TValue>> property)
        => AddMetricAgg(property, AggregationType.Sum);

    /// <summary>Compute the minimum of a numeric field.</summary>
    public QueryBuilder<T> Min<TValue>(Expression<Func<T, TValue>> property)
        => AddMetricAgg(property, AggregationType.Min);

    /// <summary>Compute the maximum of a numeric field.</summary>
    public QueryBuilder<T> Max<TValue>(Expression<Func<T, TValue>> property)
        => AddMetricAgg(property, AggregationType.Max);

    /// <summary>Count distinct values of a field.</summary>
    public QueryBuilder<T> CountDistinct<TValue>(Expression<Func<T, TValue>> property)
        => AddMetricAgg(property, AggregationType.Count);

    // ── Build ──────────────────────────────────────────────────────────────────

    public SearchRequest Build()
    {
        var query = new SearchQuery { Logic = _logic };
        query.Clauses.AddRange(_clauses);
        query.Sort.AddRange(_sorts);

        var request = new SearchRequest
        {
            TypeName = _typeName,
            Page     = _page,
            PageSize = _pageSize,
            Query    = query
        };
        request.Joins.AddRange(_joins);
        return request;
    }

    /// <summary>Builds an <see cref="AggregateRequest"/> using any clauses as the filter.</summary>
    public AggregateRequest BuildAggregate(string? traceId = null)
    {
        var query = new SearchQuery { Logic = _logic };
        query.Clauses.AddRange(_clauses);

        var request = new AggregateRequest
        {
            TypeName = _typeName,
            Query    = query,
            TraceId  = traceId ?? string.Empty
        };
        request.Aggregations.AddRange(_aggregations);
        request.Joins.AddRange(_joins);
        return request;
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private QueryBuilder<T> AddMetricAgg<TValue>(
        Expression<Func<T, TValue>> property, AggregationType type)
    {
        var field = PropertyName(property);
        _aggregations.Add(new AggregationSpec
        {
            Name  = $"{field}_{type.ToString().ToLowerInvariant()}",
            Type  = type,
            Field = field
        });
        return this;
    }

    private QueryBuilder<T> AddVectorClause<TValue>(
        Expression<Func<T, TValue>> property,
        float[] queryVector,
        SearchClauseType clauseType)
    {
        _clauses.Add(new SearchClause
        {
            Property   = PropertyName(property),
            Operator   = SearchOperator.VectorSimilar,
            Value      = new SearchValue { FloatList = new RepeatedFloat { Values = { queryVector } } },
            ClauseType = clauseType
        });
        return this;
    }

    private QueryBuilder<T> AddClause<TValue>(
        Expression<Func<T, TValue>> property,
        SearchOperator op,
        TValue value,
        SearchClauseType clauseType)
    {
        _clauses.Add(new SearchClause
        {
            Property   = PropertyName(property),
            Operator   = op,
            Value      = SearchValueConverter.ToSearchValue(value),
            ClauseType = clauseType
        });
        return this;
    }

    private static string PropertyName<TValue>(Expression<Func<T, TValue>> expr) =>
        expr.Body is MemberExpression member
            ? member.Member.Name
            : throw new ArgumentException("Expression must be a direct property access, e.g. x => x.Title");

    /// <summary>
    /// Extracts a property name from an expression typed as <c>Func&lt;TSource, object&gt;</c>.
    /// Value-type properties (e.g. <c>int</c>, <c>double</c>) are implicitly boxed when assigned
    /// to <c>object</c>, which wraps the <see cref="MemberExpression"/> in a <see cref="UnaryExpression"/>
    /// (a <see cref="ExpressionType.Convert"/> node). Reference-type properties (e.g. <c>string</c>)
    /// are not boxed and appear as a direct <see cref="MemberExpression"/>.
    /// </summary>
    private static string PropertyNameObj<TSource>(Expression<Func<TSource, object>> expr) =>
        expr.Body switch
        {
            MemberExpression member => member.Member.Name,
            UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary
                when unary.Operand is MemberExpression innerMember => innerMember.Member.Name,
            _ => throw new ArgumentException("Expression must be a direct property access, e.g. x => x.Title")
        };

}
