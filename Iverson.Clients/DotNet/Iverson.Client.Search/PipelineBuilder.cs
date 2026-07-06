using Iverson.Client.Contracts;

namespace Iverson.Client.Search;

/// <summary>
/// Fluent DSL builder that compiles to a <see cref="PipelineRequest"/>. Each
/// <see cref="Step"/> is exactly one CTE in the generated StarRocks query; steps read
/// the previous step by default or any earlier step via <see cref="PipelineStepBuilder.Reads"/>.
/// String-addressed like <see cref="GroupByBuilder"/> — pipelines put multiple sources in scope.
/// </summary>
public sealed class PipelineBuilder
{
    internal const string BaseStepName = "base";

    private readonly string _typeName;
    private readonly List<SearchClause> _baseWhere = [];
    private readonly List<PipelineStep> _steps     = [];
    private readonly List<SearchSort>   _orderBy   = [];
    private SearchLogic _baseLogic = SearchLogic.And;
    private int         _limit     = 10_000;

    internal PipelineBuilder(string typeName) => _typeName = typeName;

    // ── Base-step filter ────────────────────────────────────────────────────────

    public PipelineBuilder Where(string field, SearchOperator op, object value)
        => AddBaseClause(field, op, value, SearchClauseType.Filter);

    public PipelineBuilder Not(string field, SearchOperator op, object value)
        => AddBaseClause(field, op, value, SearchClauseType.MustNot);

    public PipelineBuilder WithLogic(SearchLogic logic)
    {
        _baseLogic = logic;
        return this;
    }

    // ── Steps ───────────────────────────────────────────────────────────────────

    public PipelineBuilder Step(string name, Action<PipelineStepBuilder> configure)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Step name must be non-empty.");
        if (name.Equals(BaseStepName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Step name '{name}' is reserved for the implicit base step.");
        if (_steps.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Duplicate step name '{name}'.");

        var earlier = _steps.Select(s => s.Name).Prepend(BaseStepName).ToList();
        var builder = new PipelineStepBuilder(name, earlier);
        configure(builder);
        _steps.Add(builder.BuildStep());
        return this;
    }

    // ── Final ordering and paging ───────────────────────────────────────────────

    public PipelineBuilder SortOn(string field, bool descending = false)
    {
        _orderBy.Add(new SearchSort { Property = field, Descending = descending });
        return this;
    }

    public PipelineBuilder Limit(int n)
    {
        _limit = n;
        return this;
    }

    // ── Build ───────────────────────────────────────────────────────────────────

    public PipelineRequest Build(string? traceId = null)
    {
        var request = new PipelineRequest
        {
            TypeName  = _typeName,
            BaseLogic = _baseLogic,
            Limit     = _limit,
            TraceId   = traceId ?? string.Empty
        };
        request.BaseWhere.AddRange(_baseWhere);
        request.Steps.AddRange(_steps);
        request.OrderBy.AddRange(_orderBy);
        return request;
    }

    private PipelineBuilder AddBaseClause(
        string field, SearchOperator op, object value, SearchClauseType clauseType)
    {
        _baseWhere.Add(new SearchClause
        {
            Property   = field,
            Operator   = op,
            Value      = SearchValueConverter.ToSearchValue(value),
            ClauseType = clauseType
        });
        return this;
    }
}

/// <summary>
/// Builds one pipeline step (= one CTE): WHERE + (window functions XOR
/// GROUP BY/metrics/HAVING) + derived columns + joins + projection.
/// </summary>
public sealed class PipelineStepBuilder
{
    private readonly string _name;
    private readonly IReadOnlyList<string> _earlierSteps;
    private readonly PipelineStep _step;

    internal PipelineStepBuilder(string name, IReadOnlyList<string> earlierSteps)
    {
        _name         = name;
        _earlierSteps = earlierSteps;
        _step         = new PipelineStep { Name = name };
    }

    // ── Input selection ─────────────────────────────────────────────────────────

    /// <summary>Reads any earlier named step (or "base") instead of the previous step.</summary>
    public PipelineStepBuilder Reads(string stepName)
    {
        if (!_earlierSteps.Any(s => s.Equals(stepName, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException(
                $"Step '{_name}': reads '{stepName}' does not name an earlier step.");
        _step.Reads = stepName;
        return this;
    }

    // ── Filtering ───────────────────────────────────────────────────────────────

    public PipelineStepBuilder Where(string field, SearchOperator op, object value)
        => AddClause(field, op, value, SearchClauseType.Filter);

    public PipelineStepBuilder Not(string field, SearchOperator op, object value)
        => AddClause(field, op, value, SearchClauseType.MustNot);

    public PipelineStepBuilder WithLogic(SearchLogic logic)
    {
        _step.WhereLogic = logic;
        return this;
    }

    // ── Window functions ────────────────────────────────────────────────────────

    public PipelineStepBuilder RowNumber(
        string alias, string orderBy, bool descending = false, string? partitionBy = null)
        => AddWindow(alias, WindowFunctionKind.RowNumber, "", orderBy, descending, partitionBy);

    public PipelineStepBuilder Rank(
        string alias, string orderBy, bool descending = false, string? partitionBy = null)
        => AddWindow(alias, WindowFunctionKind.Rank, "", orderBy, descending, partitionBy);

    public PipelineStepBuilder DenseRank(
        string alias, string orderBy, bool descending = false, string? partitionBy = null)
        => AddWindow(alias, WindowFunctionKind.DenseRank, "", orderBy, descending, partitionBy);

    public PipelineStepBuilder RunningSum(
        string alias, string field, string orderBy, bool descending = false, string? partitionBy = null)
        => AddWindow(alias, WindowFunctionKind.RunningSum, field, orderBy, descending, partitionBy);

    public PipelineStepBuilder RunningAvg(
        string alias, string field, string orderBy, bool descending = false, string? partitionBy = null)
        => AddWindow(alias, WindowFunctionKind.RunningAvg, field, orderBy, descending, partitionBy);

    public PipelineStepBuilder Lag(
        string alias, string field, string orderBy,
        int offset = 1, bool descending = false, string? partitionBy = null)
        => AddWindow(alias, WindowFunctionKind.Lag, field, orderBy, descending, partitionBy, offset);

    public PipelineStepBuilder Lead(
        string alias, string field, string orderBy,
        int offset = 1, bool descending = false, string? partitionBy = null)
        => AddWindow(alias, WindowFunctionKind.Lead, field, orderBy, descending, partitionBy, offset);

    // ── Aggregation ─────────────────────────────────────────────────────────────

    public PipelineStepBuilder GroupBy(string field, DateTrunc dateTrunc = DateTrunc.None)
    {
        _step.GroupBy.Add(new GroupKey { Field = field, DateTrunc = dateTrunc });
        return this;
    }

    public PipelineStepBuilder Sum(string field, string? alias = null)
        => AddMetric(alias ?? $"{field}_sum", AggregationType.Sum, field: field);
    public PipelineStepBuilder Avg(string field, string? alias = null)
        => AddMetric(alias ?? $"{field}_avg", AggregationType.Avg, field: field);
    public PipelineStepBuilder Min(string field, string? alias = null)
        => AddMetric(alias ?? $"{field}_min", AggregationType.Min, field: field);
    public PipelineStepBuilder Max(string field, string? alias = null)
        => AddMetric(alias ?? $"{field}_max", AggregationType.Max, field: field);
    public PipelineStepBuilder Count(string field, string? alias = null)
        => AddMetric(alias ?? $"{field}_count", AggregationType.Count, field: field);
    public PipelineStepBuilder CountAll(string alias = "count")
        => AddMetric(alias, AggregationType.Count);
    public PipelineStepBuilder SumExpr(string expression, string alias)
        => AddMetric(alias, AggregationType.Sum, expression: expression);
    public PipelineStepBuilder AvgExpr(string expression, string alias)
        => AddMetric(alias, AggregationType.Avg, expression: expression);

    public PipelineStepBuilder Having(string alias, SearchOperator op, object value)
    {
        _step.Having.Add(new SearchClause
        {
            Property   = alias,
            Operator   = op,
            Value      = SearchValueConverter.ToSearchValue(value),
            ClauseType = SearchClauseType.Filter
        });
        return this;
    }

    // ── Derived columns ─────────────────────────────────────────────────────────

    public PipelineStepBuilder Derive(string alias, string expr)
    {
        _step.Derive.Add(new DeriveColumn { Alias = alias, Expr = expr });
        return this;
    }

    // ── Joins and projection ────────────────────────────────────────────────────

    /// <summary>Joins the step's input against an earlier step's CTE or a registered entity type.</summary>
    public PipelineStepBuilder Join(
        string source, (string Left, string Right) on, JoinKind kind = JoinKind.Inner)
        => Join(source, [on], kind);

    public PipelineStepBuilder Join(
        string source, IReadOnlyList<(string Left, string Right)> on, JoinKind kind = JoinKind.Inner)
    {
        var join = new PipelineJoin { Source = source, Kind = kind };
        foreach (var (left, right) in on)
            join.On.Add(new JoinCondition { Left = left, Right = right });
        _step.Joins.Add(join);
        return this;
    }

    public PipelineStepBuilder Select(Action<SelectSpecBuilder> configure)
    {
        var builder = new SelectSpecBuilder();
        configure(builder);
        _step.Select.AddRange(builder.Items);
        return this;
    }

    // ── Build + validation ──────────────────────────────────────────────────────

    internal PipelineStep BuildStep()
    {
        var isAggregate = _step.GroupBy.Count > 0 || _step.Metrics.Count > 0 || _step.Having.Count > 0;

        if (_step.Windows.Count > 0 && isAggregate)
            throw new ArgumentException(
                $"Step '{_name}': window functions and GROUP BY/metrics/HAVING cannot share a step.");
        if ((_step.Metrics.Count > 0 || _step.Having.Count > 0) && _step.GroupBy.Count == 0)
            throw new ArgumentException(
                $"Step '{_name}': metrics/HAVING require at least one GroupBy key.");
        if (_step.Joins.Count > 0 && _step.Select.Count == 0)
            throw new ArgumentException(
                $"Step '{_name}': a step with joins requires a select projection.");

        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in _step.Windows.Select(w => w.Alias)
                     .Concat(_step.Derive.Select(d => d.Alias))
                     .Concat(_step.Metrics.Select(m => m.Name))
                     .Concat(_step.Select.Where(s => !string.IsNullOrEmpty(s.Alias)).Select(s => s.Alias)))
        {
            if (!aliases.Add(a))
                throw new ArgumentException($"Step '{_name}': duplicate output alias '{a}'.");
        }

        return _step;
    }

    private PipelineStepBuilder AddClause(
        string field, SearchOperator op, object value, SearchClauseType clauseType)
    {
        _step.Where.Add(new SearchClause
        {
            Property   = field,
            Operator   = op,
            Value      = SearchValueConverter.ToSearchValue(value),
            ClauseType = clauseType
        });
        return this;
    }

    private PipelineStepBuilder AddWindow(
        string alias, WindowFunctionKind kind, string field, string orderBy,
        bool descending, string? partitionBy, int offset = 1)
    {
        _step.Windows.Add(new WindowFunction
        {
            Alias       = alias,
            Kind        = kind,
            Field       = field,
            OrderBy     = orderBy,
            Descending  = descending,
            PartitionBy = partitionBy ?? string.Empty,
            Offset      = offset
        });
        return this;
    }

    private PipelineStepBuilder AddMetric(
        string alias, AggregationType type, string? field = null, string? expression = null)
    {
        var spec = new MetricSpec { Name = alias, Type = type };
        if (field is not null)      spec.Field      = field;
        if (expression is not null) spec.Expression = expression;
        _step.Metrics.Add(spec);
        return this;
    }
}

/// <summary>Builds a joined step's projection: which columns survive the join.</summary>
public sealed class SelectSpecBuilder
{
    internal List<SelectItem> Items { get; } = [];

    /// <summary>All columns from a source ("base", a step name, or a joined type name).</summary>
    public SelectSpecBuilder AllFrom(string source)
    {
        Items.Add(new SelectItem { Source = source, All = true });
        return this;
    }

    /// <summary>One column from a source, optionally renamed.</summary>
    public SelectSpecBuilder Pick(string source, string column, string? alias = null)
    {
        Items.Add(new SelectItem
        {
            Source = source,
            Column = column,
            Alias  = alias ?? string.Empty
        });
        return this;
    }
}
