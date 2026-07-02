using Iverson.Client.Contracts;

namespace Iverson.Client.Search;

/// <summary>
/// Fluent DSL builder that compiles to a <see cref="GroupByRequest"/>.
/// Not generic on a type parameter — joins bring multiple registered types into scope,
/// so keys, filters, and metrics are addressed by raw field-name strings.
/// </summary>
public sealed class GroupByBuilder
{
    private readonly string             _typeName;
    private readonly List<string>       _keys    = [];
    private readonly List<MetricSpec>   _metrics = [];
    private readonly List<SearchSort>   _orderBy = [];
    private readonly List<SearchClause> _where   = [];
    private readonly List<SearchClause> _having  = [];
    private readonly List<JoinSpec>     _joins   = [];
    private SearchLogic _whereLogic = SearchLogic.And;
    private int          _limit     = 10_000;

    public GroupByBuilder(string typeName) => _typeName = typeName;

    // ── Keys ────────────────────────────────────────────────────────────────────

    /// <summary>Adds a single GROUP BY key.</summary>
    public GroupByBuilder Key(string field)
    {
        _keys.Add(field);
        return this;
    }

    /// <summary>Adds multiple GROUP BY keys.</summary>
    public GroupByBuilder Keys(params string[] fields)
    {
        _keys.AddRange(fields);
        return this;
    }

    // ── WHERE filter (raw field strings, same operators as QueryBuilder) ─────────

    public GroupByBuilder Where(string field, SearchOperator op, object value)
        => AddWhere(field, op, value, SearchClauseType.Filter);

    public GroupByBuilder WithLogic(SearchLogic logic)
    {
        _whereLogic = logic;
        return this;
    }

    // ── HAVING (references output alias names) ────────────────────────────────────

    public GroupByBuilder Having(string alias, SearchOperator op, object value)
        => AddHaving(alias, op, value);

    // ── JOIN ────────────────────────────────────────────────────────────────────

    public GroupByBuilder Join(
        string leftType, string rightType,
        string leftField, string rightField,
        JoinKind kind = JoinKind.Inner)
    {
        _joins.Add(new JoinSpec
        {
            LeftType   = leftType,
            RightType  = rightType,
            LeftField  = leftField,
            RightField = rightField,
            Kind       = kind
        });
        return this;
    }

    // ── Metrics — simple field ─────────────────────────────────────────────────

    public GroupByBuilder Sum(string field, string? alias = null)
        => AddMetric(alias ?? $"{field}_sum", AggregationType.Sum, field: field);

    public GroupByBuilder Avg(string field, string? alias = null)
        => AddMetric(alias ?? $"{field}_avg", AggregationType.Avg, field: field);

    public GroupByBuilder Min(string field, string? alias = null)
        => AddMetric(alias ?? $"{field}_min", AggregationType.Min, field: field);

    public GroupByBuilder Max(string field, string? alias = null)
        => AddMetric(alias ?? $"{field}_max", AggregationType.Max, field: field);

    public GroupByBuilder Count(string field, string? alias = null)
        => AddMetric(alias ?? $"{field}_count", AggregationType.Count, field: field);

    /// <summary>COUNT(*) — leaves the metric's field empty.</summary>
    public GroupByBuilder CountAll(string alias = "count")
        => AddMetric(alias, AggregationType.Count);

    // ── Metrics — expression (raw SQL) ─────────────────────────────────────────

    public GroupByBuilder SumExpr(string expression, string alias)
        => AddMetric(alias, AggregationType.Sum, expression: expression);

    public GroupByBuilder AvgExpr(string expression, string alias)
        => AddMetric(alias, AggregationType.Avg, expression: expression);

    // ── Sorting and limit ───────────────────────────────────────────────────────

    public GroupByBuilder OrderBy(string field, bool descending = false)
    {
        _orderBy.Add(new SearchSort { Property = field, Descending = descending });
        return this;
    }

    public GroupByBuilder Limit(int n)
    {
        _limit = n;
        return this;
    }

    // ── Build ──────────────────────────────────────────────────────────────────

    public GroupByRequest Build(string? traceId = null)
    {
        var request = new GroupByRequest
        {
            TypeName = _typeName,
            Query    = new SearchQuery { Logic = _whereLogic },
            Having   = new SearchQuery { Logic = SearchLogic.And },
            Limit    = _limit,
            TraceId  = traceId ?? string.Empty
        };
        request.Keys.AddRange(_keys);
        request.Metrics.AddRange(_metrics);
        request.Query.Clauses.AddRange(_where);
        request.Having.Clauses.AddRange(_having);
        request.OrderBy.AddRange(_orderBy);
        request.Joins.AddRange(_joins);
        return request;
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private GroupByBuilder AddMetric(
        string alias, AggregationType type, string? field = null, string? expression = null)
    {
        var spec = new MetricSpec { Name = alias, Type = type };
        if (field is not null)      spec.Field      = field;
        if (expression is not null) spec.Expression = expression;
        _metrics.Add(spec);
        return this;
    }

    private GroupByBuilder AddWhere(string field, SearchOperator op, object value, SearchClauseType clauseType)
    {
        _where.Add(new SearchClause
        {
            Property   = field,
            Operator   = op,
            Value      = ToSearchValue(value),
            ClauseType = clauseType
        });
        return this;
    }

    private GroupByBuilder AddHaving(string alias, SearchOperator op, object value)
    {
        _having.Add(new SearchClause
        {
            Property   = alias,
            Operator   = op,
            Value      = ToSearchValue(value),
            ClauseType = SearchClauseType.Filter
        });
        return this;
    }

    private static SearchValue ToSearchValue(object? value) => value switch
    {
        null          => new SearchValue(),
        string s      => new SearchValue { StringVal  = s },
        bool b        => new SearchValue { BoolVal    = b },
        float f       => new SearchValue { NumberVal  = f },
        double d      => new SearchValue { NumberVal  = d },
        int i         => new SearchValue { NumberVal  = i },
        long l        => new SearchValue { NumberVal  = l },
        DateTime dt   => new SearchValue { StringVal  = dt.ToString("o") },
        DateTimeOffset dto => new SearchValue { StringVal = dto.ToString("o") },

        // IN operator: IEnumerable<string>
        IEnumerable<string> strings => new SearchValue
        {
            StringList = new RepeatedString { Values = { strings } }
        },

        // Fallback: toString
        _ => new SearchValue { StringVal = value.ToString() ?? string.Empty }
    };
}
