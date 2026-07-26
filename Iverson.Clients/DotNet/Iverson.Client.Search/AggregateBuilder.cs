using Iverson.Client.Contracts;

namespace Iverson.Client.Search;

/// <summary>
/// Fluent DSL builder that compiles to an <see cref="AggregateRequest"/>.
/// Not generic on a type parameter — joins bring multiple registered types into scope,
/// so filters and aggregation fields are addressed by raw field-name strings, same as
/// <see cref="GroupByBuilder"/>. Unlike <see cref="GroupByBuilder"/> (one compound SELECT
/// with all metrics as columns), each entry here becomes its own <see cref="AggregationSpec"/>
/// (one SQL query per spec on the server).
/// </summary>
public sealed class AggregateBuilder
{
    private readonly string                 _typeName;
    private readonly List<AggregationSpec>  _aggregations = [];
    private readonly List<SearchClause>     _where        = [];
    private readonly List<SearchClause>     _having       = [];
    private readonly List<JoinSpec>         _joins        = [];
    private SearchLogic _whereLogic  = SearchLogic.And;
    private SearchLogic _havingLogic = SearchLogic.And;

    public AggregateBuilder(string typeName) => _typeName = typeName;

    // ── WHERE filter (raw field strings, same operators as QueryBuilder) ─────────

    public AggregateBuilder Where(string field, SearchOperator op, object value)
        => AddWhere(field, op, value, SearchClauseType.Filter);

    /// <summary>Adds a MUST_NOT WHERE clause (excludes matches before aggregating).</summary>
    public AggregateBuilder Not(string field, SearchOperator op, object value)
        => AddWhere(field, op, value, SearchClauseType.MustNot);

    public AggregateBuilder WithLogic(SearchLogic logic)
    {
        _whereLogic = logic;
        return this;
    }

    // ── HAVING (references the server's fixed output alias, not the metric's name) ─

    /// <summary>
    /// Adds a HAVING clause. <paramref name="alias"/> must be one of the server's fixed
    /// output column aliases — <c>metric_val</c> for Avg/Sum/Min/Max/Count, or
    /// <c>doc_count</c>/<c>bucket_key</c> for Terms/DateHistogram/Range — never the
    /// <c>name</c> passed to a metric/bucket builder method. HAVING applies to every
    /// aggregation in the request, not just this one.
    /// </summary>
    public AggregateBuilder Having(string alias, SearchOperator op, object value)
        => AddHaving(alias, op, value);

    /// <summary>Sets the logic combining HAVING clauses. Defaults to AND.</summary>
    public AggregateBuilder WithHavingLogic(SearchLogic logic)
    {
        _havingLogic = logic;
        return this;
    }

    // ── JOIN ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a join from this builder's own type (<see cref="_typeName"/>) to
    /// <paramref name="rightType"/> via the given fields. Same leftField/rightType/rightField
    /// ordering as <see cref="GroupByBuilder.Join"/> — leftType is inferred rather than passed
    /// explicitly.
    /// </summary>
    public AggregateBuilder Join(
        string leftField, string rightType, string rightField,
        JoinKind kind = JoinKind.Inner)
    {
        _joins.Add(new JoinSpec
        {
            LeftType   = _typeName,
            RightType  = rightType,
            LeftField  = leftField,
            RightField = rightField,
            Kind       = kind
        });
        return this;
    }

    // ── Aggregations — bucketing ────────────────────────────────────────────────

    public AggregateBuilder Terms(string field, string name, int size = 10)
        => AddMetric(name, AggregationType.Terms, field: field, size: size);

    public AggregateBuilder DateHistogram(string field, string name, string calendarInterval, string timeZone = "")
        => AddMetric(name, AggregationType.DateHistogram, field: field,
            calendarInterval: calendarInterval, timeZone: timeZone);

    public AggregateBuilder Range(string field, string name, IEnumerable<(string? key, double? from, double? to)> buckets)
    {
        var spec = new AggregationSpec { Name = name, Type = AggregationType.Range, Field = field };
        foreach (var (key, from, to) in buckets)
        {
            spec.RangeBuckets.Add(new RangeBucket
            {
                Key  = key ?? string.Empty,
                From = from,
                To   = to
            });
        }
        _aggregations.Add(spec);
        return this;
    }

    // ── Aggregations — metrics ─────────────────────────────────────────────────

    public AggregateBuilder Avg(string field, string name)
        => AddMetric(name, AggregationType.Avg, field: field);

    public AggregateBuilder Sum(string field, string name)
        => AddMetric(name, AggregationType.Sum, field: field);

    public AggregateBuilder Min(string field, string name)
        => AddMetric(name, AggregationType.Min, field: field);

    public AggregateBuilder Max(string field, string name)
        => AddMetric(name, AggregationType.Max, field: field);

    public AggregateBuilder Count(string field, string name)
        => AddMetric(name, AggregationType.Count, field: field);

    /// <summary>COUNT(*) — leaves the spec's field empty.</summary>
    public AggregateBuilder CountAll(string name)
        => AddMetric(name, AggregationType.Count);

    // ── Build ──────────────────────────────────────────────────────────────────

    public AggregateRequest Build(string traceId = "")
    {
        var nameSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in _aggregations)
            if (!nameSet.Add(a.Name))
                throw new InvalidOperationException($"Duplicate aggregation name '{a.Name}'.");

        var request = new AggregateRequest
        {
            TypeName = _typeName,
            Query    = new SearchQuery { Logic = _whereLogic },
            Having   = new SearchQuery { Logic = _havingLogic },
            TraceId  = traceId
        };
        request.Aggregations.AddRange(_aggregations);
        request.Query.Clauses.AddRange(_where);
        request.Having.Clauses.AddRange(_having);
        request.Joins.AddRange(_joins);
        return request;
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private AggregateBuilder AddMetric(
        string name, AggregationType type, string? field = null,
        int? size = null, string? calendarInterval = null, string? timeZone = null)
    {
        var spec = new AggregationSpec { Name = name, Type = type };
        if (field is not null)            spec.Field             = field;
        if (size is not null)             spec.Size              = size.Value;
        if (calendarInterval is not null) spec.CalendarInterval  = calendarInterval;
        if (timeZone is not null)         spec.TimeZone          = timeZone;
        _aggregations.Add(spec);
        return this;
    }

    private AggregateBuilder AddWhere(string field, SearchOperator op, object value, SearchClauseType clauseType)
    {
        _where.Add(new SearchClause
        {
            Property   = field,
            Operator   = op,
            Value      = SearchValueConverter.ToSearchValue(value),
            ClauseType = clauseType
        });
        return this;
    }

    private AggregateBuilder AddHaving(string alias, SearchOperator op, object value)
    {
        _having.Add(new SearchClause
        {
            Property   = alias,
            Operator   = op,
            Value      = SearchValueConverter.ToSearchValue(value),
            ClauseType = SearchClauseType.Filter
        });
        return this;
    }
}
