using Iverson.Client.Contracts;

namespace Iverson.Client.Search;

/// <summary>
/// Fluent builder that compiles to a <see cref="MatchPatternRequest"/> (SQL:2016 row pattern
/// recognition). Pattern, DEFINE and MEASURES strings pass through unchanged; the server parses
/// and validates them. String-addressed like <see cref="PipelineBuilder"/>.
/// </summary>
public sealed class MatchPatternBuilder
{
    private readonly string _typeName;
    private readonly List<SearchClause>  _where       = [];
    private readonly List<string>        _partitionBy = [];
    private readonly List<SearchSort>    _orderBy     = [];
    private readonly List<PatternSubset> _subsets     = [];
    private readonly List<NamedExpr>     _define      = [];
    private readonly List<NamedExpr>     _measures    = [];
    private PatternRowSource _source        = PatternRowSource.TypeRows;
    private string           _chunkProperty = string.Empty;
    private SearchLogic      _whereLogic    = SearchLogic.And;
    private string           _pattern       = string.Empty;
    private RowsPerMatch     _rowsPerMatch;   // default ONE_ROW
    private AfterMatchSkip?  _afterMatch;     // unset: the server applies PAST_LAST_ROW
    private int              _limit;          // 0: the server's default

    internal MatchPatternBuilder(string typeName) => _typeName = typeName;

    // ── Row source ──────────────────────────────────────────────────────────────

    /// <summary>Matches over the chunks of an <c>[IversonChunk]</c> property instead of the type's rows.</summary>
    public MatchPatternBuilder Chunks(string chunkProperty)
    {
        _source        = PatternRowSource.Chunks;
        _chunkProperty = chunkProperty;
        return this;
    }

    // ── Pre-filter ──────────────────────────────────────────────────────────────

    public MatchPatternBuilder Where(string field, SearchOperator op, object value)
        => AddClause(field, op, value, SearchClauseType.Filter);

    public MatchPatternBuilder Not(string field, SearchOperator op, object value)
        => AddClause(field, op, value, SearchClauseType.MustNot);

    public MatchPatternBuilder WithLogic(SearchLogic logic)
    {
        _whereLogic = logic;
        return this;
    }

    // ── Partitioning and ordering ───────────────────────────────────────────────

    public MatchPatternBuilder PartitionBy(params string[] fields)
    {
        _partitionBy.AddRange(fields);
        return this;
    }

    public MatchPatternBuilder OrderBy(string field, bool descending = false)
    {
        _orderBy.Add(new SearchSort { Property = field, Descending = descending });
        return this;
    }

    // ── Pattern ─────────────────────────────────────────────────────────────────

    public MatchPatternBuilder Pattern(string pattern)
    {
        _pattern = pattern;
        return this;
    }

    public MatchPatternBuilder Subset(string name, params string[] variables)
    {
        var subset = new PatternSubset { Name = name };
        subset.Variables.AddRange(variables);
        _subsets.Add(subset);
        return this;
    }

    public MatchPatternBuilder Define(string variable, string expr)
    {
        _define.Add(new NamedExpr { Name = variable, Expr = expr });
        return this;
    }

    public MatchPatternBuilder Measure(string name, string expr)
    {
        _measures.Add(new NamedExpr { Name = name, Expr = expr });
        return this;
    }

    // ── Output ──────────────────────────────────────────────────────────────────

    public MatchPatternBuilder RowsPerMatch(RowsPerMatch mode)
    {
        _rowsPerMatch = mode;
        return this;
    }

    /// <summary><paramref name="variable"/> applies to <c>TO_FIRST</c> and <c>TO_LAST</c> only.</summary>
    public MatchPatternBuilder AfterMatch(AfterMatchSkipKind kind, string variable = "")
    {
        _afterMatch = new AfterMatchSkip { Kind = kind, Variable = variable };
        return this;
    }

    public MatchPatternBuilder Limit(int n)
    {
        _limit = n;
        return this;
    }

    // ── Build ───────────────────────────────────────────────────────────────────

    public MatchPatternRequest Build(string? traceId = null)
    {
        var request = new MatchPatternRequest
        {
            TypeName      = _typeName,
            Source        = _source,
            ChunkProperty = _chunkProperty,
            WhereLogic    = _whereLogic,
            Pattern       = _pattern,
            RowsPerMatch  = _rowsPerMatch,
            AfterMatch    = _afterMatch,
            Limit         = _limit,
            TraceId       = traceId ?? string.Empty
        };
        request.Where.AddRange(_where);
        request.PartitionBy.AddRange(_partitionBy);
        request.OrderBy.AddRange(_orderBy);
        request.Subsets.AddRange(_subsets);
        request.Define.AddRange(_define);
        request.Measures.AddRange(_measures);
        return request;
    }

    private MatchPatternBuilder AddClause(
        string field, SearchOperator op, object value, SearchClauseType clauseType)
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
}
