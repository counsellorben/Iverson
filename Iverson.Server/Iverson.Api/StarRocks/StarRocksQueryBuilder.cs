using System.Runtime.CompilerServices;
using System.Text;
using Dapper;
using Grpc.Core;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;

using SrAggKind  = Iverson.StarRocks.AggregationKind;
using SrAggSpec  = Iverson.StarRocks.AggregationDescriptor;
using SrRangeSpec = Iverson.StarRocks.RangeBucketDescriptor;

namespace Iverson.Api.StarRocks;

/// <summary>
/// A single table participating in a joined query: its physical table name,
/// schema descriptor, and the alias used to qualify columns in generated SQL.
/// </summary>
internal sealed record JoinContext(string TableName, SchemaDescriptor Schema, string Alias);

internal static class StarRocksQueryBuilder
{
    private static readonly ConditionalWeakTable<
        SchemaDescriptor,
        Dictionary<string, string>> _columnCache = new();
    internal static (string Sql, DynamicParameters Param) BuildSearch(
        string tableName,
        SchemaDescriptor schema,
        SearchQuery? query,
        int page,
        int pageSize,
        IReadOnlyList<string>? fields = null,
        IReadOnlyList<JoinSpec>? joins = null,
        SchemaRegistry? registry = null)
    {
        var param = new DynamicParameters();

        var limit  = pageSize > 0 ? pageSize : 50;
        var offset = page > 0 ? page * limit : 0;
        var order = BuildOrder(schema, query?.Sort);

        string from;
        string where;
        string selectCols;
        if (joins is { Count: > 0 })
        {
            from = BuildFromWithJoins(schema, joins, registry!, out var tableMap);
            where = BuildWhere(schema, query?.Clauses, query?.Logic ?? SearchLogic.And, param, out _, tableMap);

            // The primary table's own columns must be qualified with its alias once a JOIN is
            // in play — an unqualified `Id` (or any other column name shared with a joined
            // table, e.g. every table's key column) is ambiguous SQL and StarRocks rejects the
            // query at analysis time. BuildAggregate already qualifies its SELECT expressions
            // this way (see its local Quote/Resolve helpers above); BuildSelectColumns needs the
            // same treatment for BuildSearch's plain column list.
            var primaryAlias = tableMap[schema.TypeName].Alias;
            selectCols = BuildSelectColumns(schema, fields, primaryAlias);
        }
        else
        {
            from = $"FROM `{tableName}`";
            where = BuildWhere(schema, query?.Clauses, query?.Logic ?? SearchLogic.And, param, out _);
            selectCols = BuildSelectColumns(schema, fields);
        }

        var sb = new StringBuilder($"SELECT {selectCols} {from}");
        if (where.Length > 0) sb.Append($" WHERE {where}");
        if (order.Length > 0) sb.Append($" ORDER BY {order}");
        sb.Append($" LIMIT {limit} OFFSET {offset}");

        return (sb.ToString(), param);
    }

    private static string BuildSelectColumns(SchemaDescriptor schema, IReadOnlyList<string>? fields, string? primaryAlias = null)
    {
        string Quote(string name) => primaryAlias is null ? $"`{name}`" : $"`{primaryAlias}`.`{name}`";

        if (fields is null || fields.Count == 0)
        {
            var all = schema.ScalarColumns
                .Select(c => Quote(c.Name))
                .Prepend(Quote(schema.KeyColumn.Name));
            return string.Join(", ", all);
        }

        var resolved = new List<string> { schema.KeyColumn.Name };
        foreach (var f in fields)
        {
            var col = ResolveColumn(schema, f);
            if (col is not null && !col.Equals(schema.KeyColumn.Name, StringComparison.OrdinalIgnoreCase))
                resolved.Add(col);
        }
        return string.Join(", ", resolved.Select(Quote));
    }

    internal static (string Sql, DynamicParameters Param) BuildAggregate(
        string tableName,
        SchemaDescriptor schema,
        SearchQuery? query,
        SrAggSpec spec,
        SearchQuery? having = null,
        IReadOnlyList<JoinSpec>? joins = null,
        SchemaRegistry? registry = null)
    {
        var param = new DynamicParameters();

        string from;
        IReadOnlyDictionary<string, JoinContext>? tableMap;
        if (joins is { Count: > 0 })
        {
            from = BuildFromWithJoins(schema, joins, registry!, out var tm);
            tableMap = tm;
        }
        else
        {
            from = $"FROM `{tableName}`";
            tableMap = null;
        }

        // Resolves a field against the joined tableMap when joins are present, otherwise
        // against the primary schema alone — mirroring the same tableMap-or-not split used
        // throughout BuildSearch/BuildGroupBy.
        string Resolve(string f) =>
            (tableMap is not null ? ResolveColumn(tableMap, f) : ResolveColumn(schema, f)) ?? f;

        // Quotes an already-resolved column: two separately-backtick-quoted "alias"."field"
        // parts when joined (see QuoteQualified's doc comment for why a single backtick pair
        // around "alias.field" is invalid SQL), or a bare single-backtick pair otherwise.
        string Quote(string c) => tableMap is not null ? QuoteQualified(c) : $"`{c}`";

        var where = tableMap is not null
            ? BuildWhere(schema, query?.Clauses, query?.Logic ?? SearchLogic.And, param, out _, tableMap)
            : BuildWhere(schema, query?.Clauses, query?.Logic ?? SearchLogic.And, param, out _);
        var col = Resolve(spec.Field);
        var wc    = where.Length > 0 ? $" WHERE {where}" : "";

        var havingSql = BuildHaving(having?.Clauses, having?.Logic ?? SearchLogic.And, param);
        var hc = havingSql.Length > 0 ? $" HAVING {havingSql}" : "";

        // Multi-key GROUP BY: spec.GroupByFields, when present with more than one entry,
        // overrides spec.Field for TERMS and selects/groups by all listed columns.
        var groupCols = spec.GroupByFields is { Count: > 1 }
            ? spec.GroupByFields.Select(Resolve).ToList()
            : null;

        var sql = spec.Kind switch
        {
            SrAggKind.Terms => groupCols is not null
                ? $"SELECT {string.Join(", ", groupCols.Select(Quote))}, COUNT(*) AS doc_count " +
                  $"{from}{wc} " +
                  $"GROUP BY {string.Join(", ", groupCols.Select(Quote))}{hc} " +
                  $"ORDER BY doc_count DESC " +
                  $"LIMIT {(spec.Size > 0 ? spec.Size : 10)}"
                : $"SELECT {Quote(col)} AS bucket_key, COUNT(*) AS doc_count " +
                  $"{from}{wc} " +
                  $"GROUP BY {Quote(col)}{hc} " +
                  $"ORDER BY doc_count DESC " +
                  $"LIMIT {(spec.Size > 0 ? spec.Size : 10)}",

            SrAggKind.DateHistogram =>
                $"SELECT {DateBucketExpr(Quote(col), spec.CalendarInterval)} AS bucket_key, " +
                $"COUNT(*) AS doc_count " +
                $"{from}{wc} " +
                $"GROUP BY bucket_key{hc} ORDER BY bucket_key",

            SrAggKind.Range => BuildRangeSql(from, Quote(col), spec.RangeBuckets, wc, hc),

            // spec.Expression, when set, is raw StarRocks SQL supplied by a trusted
            // server-side caller (e.g. TPC-H DSL translation) and is spliced directly
            // into the aggregate function — NOT user input, no escaping is performed.
            SrAggKind.Avg   => $"SELECT AVG({spec.Expression ?? Quote(col)}) AS metric_val {from}{wc}{hc}",
            SrAggKind.Sum   => $"SELECT SUM({spec.Expression ?? Quote(col)}) AS metric_val {from}{wc}{hc}",
            SrAggKind.Min   => $"SELECT MIN({spec.Expression ?? Quote(col)}) AS metric_val {from}{wc}{hc}",
            SrAggKind.Max   => $"SELECT MAX({spec.Expression ?? Quote(col)}) AS metric_val {from}{wc}{hc}",
            SrAggKind.Count => $"SELECT COUNT(DISTINCT {spec.Expression ?? Quote(col)}) AS metric_val {from}{wc}{hc}",

            _ => throw new ArgumentOutOfRangeException(nameof(spec.Kind))
        };

        return (sql, param);
    }

    /// <summary>
    /// Builds a single compound SELECT that computes multiple metrics over one GROUP BY in a
    /// single SQL round-trip (e.g. TPC-H Q1: several SUM/AVG/COUNT columns, grouped by 2 keys,
    /// ordered, HAVING-filtered). Unlike <see cref="BuildAggregate"/>, which issues one SQL
    /// query per <see cref="SrAggSpec"/>, this emits one query for the whole
    /// <see cref="GroupByRequest"/>.
    /// </summary>
    internal static (string Sql, DynamicParameters Param) BuildGroupBy(
        string primaryTable,
        SchemaDescriptor schema,
        GroupByRequest request,
        SchemaRegistry registry)
    {
        var from = BuildFromWithJoins(schema, request.Joins, registry, out var tableMap);

        var param = new DynamicParameters();
        var where = BuildWhere(schema, request.Query?.Clauses, request.Query?.Logic ?? SearchLogic.And, param, out _, tableMap);
        var wc = where.Length > 0 ? $" WHERE {where}" : "";

        var keyCols = request.Keys
            .Select(k => ResolveColumn(tableMap, k)
                ?? throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Unknown or ambiguous GROUP BY key '{k}'.")))
            .ToList();

        var metricExprs = request.Metrics.Select(m => BuildMetricExpr(m, tableMap)).ToList();

        var selectCols = keyCols.Select(QuoteQualified)
            .Concat(metricExprs)
            .ToList();

        var havingSql = BuildHaving(request.Having?.Clauses, request.Having?.Logic ?? SearchLogic.And, param);
        var hc = havingSql.Length > 0 ? $" HAVING {havingSql}" : "";

        var orderSql = request.OrderBy
            .Select(s => (col: ResolveColumn(tableMap, s.Property) ?? s.Property, s.Descending))
            .Select(x => $"{QuoteQualified(x.col)} {(x.Descending ? "DESC" : "ASC")}")
            .ToList();
        var oc = orderSql.Count > 0 ? $" ORDER BY {string.Join(", ", orderSql)}" : "";

        var limit = request.Limit > 0 ? request.Limit : 10_000;

        var sql = $"SELECT {string.Join(", ", selectCols)} {from}{wc} " +
                   $"GROUP BY {string.Join(", ", keyCols.Select(QuoteQualified))}{hc}{oc} " +
                   $"LIMIT {limit}";

        return (sql, param);
    }

    // metric.expression, when set, is raw StarRocks SQL supplied by a trusted server-side
    // caller (e.g. TPC-H DSL translation) and is spliced directly into the aggregate
    // function — NOT user input, no escaping is performed.
    private static string BuildMetricExpr(
        MetricSpec metric,
        IReadOnlyDictionary<string, JoinContext> tableMap)
    {
        var isCountAll = metric.Type == AggregationType.Count
            && string.IsNullOrEmpty(metric.Field)
            && string.IsNullOrEmpty(metric.Expression);

        var quotedName = $"`{EscapeIdentifier(metric.Name)}`";

        if (isCountAll)
            return $"COUNT(*) AS {quotedName}";

        var fn = metric.Type switch
        {
            AggregationType.Avg   => "AVG",
            AggregationType.Sum   => "SUM",
            AggregationType.Min   => "MIN",
            AggregationType.Max   => "MAX",
            AggregationType.Count => "COUNT",
            _ => throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Metric '{metric.Name}' has unsupported type '{metric.Type}'; GroupBy metrics must be AVG, SUM, MIN, MAX, or COUNT."))
        };

        if (!string.IsNullOrEmpty(metric.Expression))
            return $"{fn}({metric.Expression}) AS {quotedName}";

        if (!string.IsNullOrEmpty(metric.Field))
        {
            var col = ResolveColumn(tableMap, metric.Field)
                ?? throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Unknown or ambiguous field '{metric.Field}' referenced by metric '{metric.Name}'."));
            return $"{fn}({QuoteQualified(col)}) AS {quotedName}";
        }

        // Both Field and Expression are empty/null. COUNT(*) is the one legitimate case for
        // this (handled above via isCountAll); every other metric kind requires a column
        // argument, so emitting the naive fallback here would produce invalid SQL like
        // "SUM(``)" — fail loudly instead.
        throw new RpcException(new Status(StatusCode.InvalidArgument,
            $"metric '{metric.Name}' requires a field or expression"));
    }

    internal static string BuildWhere(
        SchemaDescriptor schema,
        IEnumerable<SearchClause>? clauses,
        SearchLogic logic,
        DynamicParameters param,
        out int nextIdx,
        IReadOnlyDictionary<string, JoinContext>? tableMap = null)
    {
        // Single quoting decision point: resolve + fully quote the column into one
        // ready-to-embed SQL identifier. Cross-schema (tableMap present) columns are
        // "alias.field" and MUST go through QuoteQualified (two separately-backtick-quoted
        // parts) — a single backtick pair around "alias.field" is invalid SQL (see
        // QuoteQualified's doc comment).
        Func<string, string?> resolve = tableMap is not null
            ? p => ResolveColumn(tableMap, p) is { } qc ? QuoteQualified(qc) : null
            : p => ResolveColumn(schema, p) is { } c ? $"`{c}`" : null;
        return BuildWhere(resolve, clauses, logic, param, "p", out nextIdx);
    }

    /// <summary>
    /// Core WHERE builder. <paramref name="resolveQuoted"/> maps a clause property to a
    /// fully-quoted, ready-to-embed SQL identifier (or null to skip the clause), and
    /// <paramref name="paramPrefix"/> names the Dapper parameters ("p" for plain queries;
    /// pipeline steps pass "s{i}_p" so multiple steps can share one DynamicParameters).
    /// </summary>
    internal static string BuildWhere(
        Func<string, string?> resolveQuoted,
        IEnumerable<SearchClause>? clauses,
        SearchLogic logic,
        DynamicParameters param,
        string paramPrefix,
        out int nextIdx)
    {
        nextIdx = 0;
        if (clauses is null) return "";

        var parts = new List<string>();

        foreach (var clause in clauses)
        {
            if (clause.Operator == SearchOperator.VectorSimilar) continue;

            var quotedCol = resolveQuoted(clause.Property);
            if (quotedCol is null) continue;

            var pName = $"{paramPrefix}{nextIdx++}";

            var condition = clause.Operator switch
            {
                SearchOperator.Equals => BuildEq(quotedCol, pName, clause.Value, param),
                SearchOperator.NotEquals =>
                    Condition($"{quotedCol} <> @{pName}", pName, GetScalarValue(clause.Value), param),
                SearchOperator.Contains =>
                    Condition($"{quotedCol} LIKE @{pName}", pName, $"%{clause.Value?.StringVal}%", param),
                SearchOperator.StartsWith =>
                    Condition($"{quotedCol} LIKE @{pName}", pName, $"{clause.Value?.StringVal}%", param),
                SearchOperator.EndsWith =>
                    Condition($"{quotedCol} LIKE @{pName}", pName, $"%{clause.Value?.StringVal}", param),
                SearchOperator.GreaterThan =>
                    Condition($"{quotedCol} > @{pName}", pName, GetScalarValue(clause.Value), param),
                SearchOperator.GreaterThanOrEquals =>
                    Condition($"{quotedCol} >= @{pName}", pName, GetScalarValue(clause.Value), param),
                SearchOperator.LessThan =>
                    Condition($"{quotedCol} < @{pName}", pName, GetScalarValue(clause.Value), param),
                SearchOperator.LessThanOrEquals =>
                    Condition($"{quotedCol} <= @{pName}", pName, GetScalarValue(clause.Value), param),
                SearchOperator.In => BuildIn(quotedCol, pName, clause.Value, param),
                _ => null
            };

            if (condition is null) continue;

            var wrapped = clause.ClauseType == SearchClauseType.MustNot
                ? $"NOT ({condition})"
                : condition;

            parts.Add(wrapped);
        }

        if (parts.Count == 0) return "";
        var sep = logic == SearchLogic.Or ? " OR " : " AND ";
        return string.Join(sep, parts);
    }

    /// <summary>
    /// Builds a HAVING clause from the same clause-matching logic as <see cref="BuildWhere(SchemaDescriptor, IEnumerable{SearchClause}?, SearchLogic, DynamicParameters, out int, IReadOnlyDictionary{string, JoinContext}?)"/>,
    /// but without the schema-backed <see cref="ResolveColumn(SchemaDescriptor, string)"/> guard —
    /// HAVING clauses reference SQL output aliases (e.g. "doc_count", "metric_val") which are not
    /// schema columns, so the clause's Property is used verbatim as the column name. Uses an
    /// "h{n}" parameter prefix by default (vs. "p{n}" for WHERE) so both can share one
    /// DynamicParameters instance without name collisions when a query has both a filter and a
    /// HAVING clause; pipeline steps pass "s{i}_h" so multiple steps can share one instance too.
    /// </summary>
    internal static string BuildHaving(
        IEnumerable<SearchClause>? clauses,
        SearchLogic logic,
        DynamicParameters param,
        string paramPrefix = "h")
    {
        if (clauses is null) return "";

        var parts = new List<string>();
        var nextIdx = 0;

        foreach (var clause in clauses)
        {
            if (clause.Operator == SearchOperator.VectorSimilar) continue;

            var col = clause.Property;
            if (string.IsNullOrEmpty(col)) continue;
            var quotedCol = $"`{EscapeIdentifier(col)}`";

            var pName = $"{paramPrefix}{nextIdx++}";

            var condition = clause.Operator switch
            {
                SearchOperator.Equals => BuildEq(quotedCol, pName, clause.Value, param),
                SearchOperator.NotEquals =>
                    Condition($"{quotedCol} <> @{pName}", pName, GetScalarValue(clause.Value), param),
                SearchOperator.Contains =>
                    Condition($"{quotedCol} LIKE @{pName}", pName, $"%{clause.Value?.StringVal}%", param),
                SearchOperator.StartsWith =>
                    Condition($"{quotedCol} LIKE @{pName}", pName, $"{clause.Value?.StringVal}%", param),
                SearchOperator.EndsWith =>
                    Condition($"{quotedCol} LIKE @{pName}", pName, $"%{clause.Value?.StringVal}", param),
                SearchOperator.GreaterThan =>
                    Condition($"{quotedCol} > @{pName}", pName, GetScalarValue(clause.Value), param),
                SearchOperator.GreaterThanOrEquals =>
                    Condition($"{quotedCol} >= @{pName}", pName, GetScalarValue(clause.Value), param),
                SearchOperator.LessThan =>
                    Condition($"{quotedCol} < @{pName}", pName, GetScalarValue(clause.Value), param),
                SearchOperator.LessThanOrEquals =>
                    Condition($"{quotedCol} <= @{pName}", pName, GetScalarValue(clause.Value), param),
                SearchOperator.In => BuildIn(quotedCol, pName, clause.Value, param),
                _ => null
            };

            if (condition is null) continue;

            var wrapped = clause.ClauseType == SearchClauseType.MustNot
                ? $"NOT ({condition})"
                : condition;

            parts.Add(wrapped);
        }

        if (parts.Count == 0) return "";
        var sep = logic == SearchLogic.Or ? " OR " : " AND ";
        return string.Join(sep, parts);
    }

    internal static string? ResolveColumn(SchemaDescriptor schema, string property)
    {
        var index = _columnCache.GetValue(schema, static s =>
            s.ScalarColumns
                .Select(c => c.Name)
                .Append(s.KeyColumn.Name)
                .ToDictionary(n => n, n => n, StringComparer.OrdinalIgnoreCase));

        return index.TryGetValue(property, out var col) ? col : null;
    }

    /// <summary>
    /// Resolves a column across multiple joined schemas. Accepts either
    /// "TypeName.FieldName" dot notation, or a bare field name that is
    /// unambiguous across all joined schemas. Returns null if the property
    /// cannot be resolved (unknown type, unknown field, or ambiguous bare name).
    /// </summary>
    internal static string? ResolveColumn(
        IReadOnlyDictionary<string, JoinContext> tableMap,
        string property)
    {
        var dotIdx = property.IndexOf('.');
        if (dotIdx > 0)
        {
            var typeName = property[..dotIdx];
            var fieldName = property[(dotIdx + 1)..];

            return tableMap.TryGetValue(typeName, out var ctx)
                ? ResolveColumn(ctx.Schema, fieldName) is { } col ? $"{ctx.Alias}.{col}" : null
                : null;
        }

        string? match = null;
        foreach (var ctx in tableMap.Values)
        {
            var col = ResolveColumn(ctx.Schema, property);
            if (col is null) continue;

            if (match is not null) return null; // ambiguous across joined schemas
            match = $"{ctx.Alias}.{col}";
        }

        return match;
    }

    /// <summary>
    /// Quotes an "alias.field" string (as returned by <see cref="ResolveColumn(IReadOnlyDictionary{string, JoinContext}, string)"/>)
    /// as a proper table-qualified SQL identifier: two separately-backtick-quoted parts joined
    /// by an unquoted dot, e.g. <c>`authors`.`Name`</c>. A single backtick pair around the whole
    /// string (e.g. <c>`authors.Name`</c>) is WRONG — in MySQL-wire SQL a backtick-quoted token
    /// does not split on '.', so that form is parsed as one identifier literally named
    /// "authors.Name", which does not exist. Splits on the LAST '.' rather than the first, since
    /// the alias itself never contains a dot but a field name theoretically could.
    /// </summary>
    private static string QuoteQualified(string aliasDotColumn)
    {
        var dotIdx = aliasDotColumn.LastIndexOf('.');
        if (dotIdx < 0) return $"`{aliasDotColumn}`";

        var alias = aliasDotColumn[..dotIdx];
        var field = aliasDotColumn[(dotIdx + 1)..];
        return $"`{alias}`.`{field}`";
    }

    /// <summary>
    /// Builds a FROM clause with zero or more JOINs from a list of <see cref="JoinSpec"/>s,
    /// resolving each side's type against the <see cref="SchemaRegistry"/>. Always populates
    /// <paramref name="tableMap"/> with at least the primary table (keyed by
    /// <paramref name="primarySchema"/>'s <c>TypeName</c>), plus every joined type name, each
    /// mapped to its resolved <see cref="JoinContext"/> so callers can later qualify columns
    /// per-table — including in the no-join case.
    /// </summary>
    internal static string BuildFromWithJoins(
        SchemaDescriptor primarySchema,
        IReadOnlyList<JoinSpec> joins,
        SchemaRegistry registry,
        out IReadOnlyDictionary<string, JoinContext> tableMap)
    {
        var map = new Dictionary<string, JoinContext>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder($"FROM `{primarySchema.TableName}`");

        // Always seed the map with the primary table's own schema, regardless of whether
        // there are any joins, so callers can resolve columns against tableMap unconditionally.
        map[primarySchema.TypeName] = new JoinContext(primarySchema.TableName, primarySchema, primarySchema.TableName);

        if (joins.Count == 0)
        {
            tableMap = map;
            return sb.ToString();
        }

        foreach (var join in joins)
        {
            if (!map.TryGetValue(join.LeftType, out var leftCtx))
            {
                var leftSchema = registry.Get(join.LeftType)
                    ?? throw new RpcException(new Status(StatusCode.InvalidArgument,
                        $"Unknown type '{join.LeftType}' referenced in join."));
                leftCtx = new JoinContext(leftSchema.TableName, leftSchema, leftSchema.TableName);
                map[join.LeftType] = leftCtx;
            }

            var rightSchema = registry.Get(join.RightType)
                ?? throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Unknown type '{join.RightType}' referenced in join."));
            var rightCtx = new JoinContext(rightSchema.TableName, rightSchema, rightSchema.TableName);

            var leftCol = ResolveColumn(leftCtx.Schema, join.LeftField)
                ?? throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Unknown field '{join.LeftField}' on type '{join.LeftType}' referenced in join."));
            var rightCol = ResolveColumn(rightCtx.Schema, join.RightField)
                ?? throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"Unknown field '{join.RightField}' on type '{join.RightType}' referenced in join."));

            var kind = join.Kind switch
            {
                JoinKind.Left  => "LEFT",
                JoinKind.Right => "RIGHT",
                JoinKind.Full  => "FULL",
                _              => "INNER"
            };

            sb.Append(
                $" {kind} JOIN `{rightCtx.TableName}` ON " +
                $"`{leftCtx.Alias}`.`{leftCol}` = `{rightCtx.Alias}`.`{rightCol}`");

            map[join.RightType] = rightCtx;
        }

        tableMap = map;
        return sb.ToString();
    }

    private static string BuildOrder(SchemaDescriptor schema, IEnumerable<SearchSort>? sorts)
    {
        if (sorts is null) return "";
        var parts = sorts
            .Select(s => (col: ResolveColumn(schema, s.Property), s.Descending))
            .Where(x => x.col is not null)
            .Select(x => $"`{x.col}` {(x.Descending ? "DESC" : "ASC")}");
        return string.Join(", ", parts);
    }

    /// <summary>
    /// <paramref name="from"/> must be a complete, ready-to-embed FROM clause (e.g.
    /// <c>FROM `authors`</c> or the multi-table form emitted by <see cref="BuildFromWithJoins"/>),
    /// and <paramref name="quotedCol"/> must already be fully quoted — see <see cref="BuildEq"/>
    /// for the equivalent contract on WHERE-clause columns.
    /// </summary>
    private static string BuildRangeSql(
        string from, string quotedCol,
        IReadOnlyList<SrRangeSpec>? buckets, string wc, string hc = "")
    {
        if (buckets is null || buckets.Count == 0)
            return $"SELECT NULL AS bucket_key, COUNT(*) AS doc_count {from}{wc}{hc}";

        var cases = buckets.Select(b =>
        {
            var key = EscapeSqlString(b.Key);
            if (b.From is null && b.To is not null)
                return $"WHEN {quotedCol} < {b.To.Value} THEN '{key}'";
            if (b.From is not null && b.To is null)
                return $"WHEN {quotedCol} >= {b.From.Value} THEN '{key}'";
            if (b.From is not null && b.To is not null)
                return $"WHEN {quotedCol} >= {b.From.Value} AND {quotedCol} < {b.To.Value} THEN '{key}'";
            return null;
        }).OfType<string>();

        return $"SELECT CASE {string.Join(" ", cases)} END AS bucket_key, " +
               $"COUNT(*) AS doc_count {from}{wc} GROUP BY bucket_key{hc}";
    }

    /// <summary>
    /// <paramref name="quotedCol"/> must already be a fully-quoted, ready-to-embed SQL
    /// identifier (e.g. <c>`Name`</c> or <c>`authors`.`Name`</c>) — callers resolve and quote
    /// once up front (see <see cref="BuildWhere"/>/<see cref="BuildHaving"/>); this method does
    /// no further backtick-wrapping.
    /// </summary>
    private static string? BuildEq(string quotedCol, string pName, SearchValue? val, DynamicParameters param)
    {
        param.Add(pName, GetScalarValue(val));
        return $"{quotedCol} = @{pName}";
    }

    /// <summary>
    /// <paramref name="quotedCol"/> must already be a fully-quoted, ready-to-embed SQL
    /// identifier — see <see cref="BuildEq"/> for the contract.
    /// </summary>
    private static string? BuildIn(string quotedCol, string pName, SearchValue? val, DynamicParameters param)
    {
        var list = val?.StringList?.Values.ToList() ?? [];
        if (list.Count == 0) return null;
        param.Add(pName, list);
        return $"{quotedCol} IN @{pName}";
    }

    private static string Condition(string expr, string pName, object? value, DynamicParameters param)
    {
        param.Add(pName, value);
        return expr;
    }

    private static object? GetScalarValue(SearchValue? v) => v?.KindCase switch
    {
        SearchValue.KindOneofCase.StringVal => (object?)v.StringVal,
        SearchValue.KindOneofCase.NumberVal => v.NumberVal,
        SearchValue.KindOneofCase.BoolVal   => v.BoolVal,
        _                                   => null
    };

    // StarRocks DATE_FORMAT has no quarter directive, so quarter is composed
    // explicitly via QUARTER(); all other intervals map to a DATE_FORMAT pattern.
    // quotedCol must already be fully quoted (e.g. `Col` or `alias`.`Col`) — see BuildEq's
    // contract for the same convention.
    private static string DateBucketExpr(string quotedCol, string? interval) =>
        interval?.ToLowerInvariant() == "quarter"
            ? $"CONCAT(YEAR({quotedCol}), '-Q', QUARTER({quotedCol}))"
            : $"DATE_FORMAT({quotedCol}, '{DateFormatFor(interval)}')";

    private static string DateFormatFor(string? interval) => interval?.ToLowerInvariant() switch
    {
        "minute"  => "%Y-%m-%d %H:%i",
        "hour"    => "%Y-%m-%d %H",
        "day"     => "%Y-%m-%d",
        "week"    => "%Y-%u",
        "month"   => "%Y-%m",
        "year"    => "%Y",
        _         => "%Y-%m"
    };

    private static string EscapeSqlString(string value) => value.Replace("'", "''");

    // Escapes an embedded backtick in a developer-supplied identifier (metric alias / HAVING
    // property) before it is wrapped in backticks — otherwise a literal backtick would close
    // the identifier early and corrupt the generated SQL. Scope is intentionally limited to
    // BuildMetricExpr and BuildHaving; other identifier sites (e.g. schema column names) are
    // not developer-supplied free text and don't need this.
    private static string EscapeIdentifier(string value) => value.Replace("`", "``");
}
