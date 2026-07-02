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
        IReadOnlyList<string>? fields = null)
    {
        var param = new DynamicParameters();
        var where = BuildWhere(schema, query?.Clauses, query?.Logic ?? SearchLogic.And, param, out _);
        var order = BuildOrder(schema, query?.Sort);

        var limit  = pageSize > 0 ? pageSize : 50;
        var offset = page > 0 ? page * limit : 0;

        var selectCols = BuildSelectColumns(schema, fields);
        var sb = new StringBuilder($"SELECT {selectCols} FROM `{tableName}`");
        if (where.Length > 0) sb.Append($" WHERE {where}");
        if (order.Length > 0) sb.Append($" ORDER BY {order}");
        sb.Append($" LIMIT {limit} OFFSET {offset}");

        return (sb.ToString(), param);
    }

    private static string BuildSelectColumns(SchemaDescriptor schema, IReadOnlyList<string>? fields)
    {
        if (fields is null || fields.Count == 0)
        {
            var all = schema.ScalarColumns
                .Select(c => $"`{c.Name}`")
                .Prepend($"`{schema.KeyColumn.Name}`");
            return string.Join(", ", all);
        }

        var resolved = new List<string> { schema.KeyColumn.Name };
        foreach (var f in fields)
        {
            var col = ResolveColumn(schema, f);
            if (col is not null && !col.Equals(schema.KeyColumn.Name, StringComparison.OrdinalIgnoreCase))
                resolved.Add(col);
        }
        return string.Join(", ", resolved.Select(c => $"`{c}`"));
    }

    internal static (string Sql, DynamicParameters Param) BuildAggregate(
        string tableName,
        SchemaDescriptor schema,
        SearchQuery? query,
        SrAggSpec spec)
    {
        var param = new DynamicParameters();
        var where = BuildWhere(schema, query?.Clauses, query?.Logic ?? SearchLogic.And, param, out _);
        var col   = ResolveColumn(schema, spec.Field) ?? spec.Field;
        var wc    = where.Length > 0 ? $" WHERE {where}" : "";

        var sql = spec.Kind switch
        {
            SrAggKind.Terms =>
                $"SELECT `{col}` AS bucket_key, COUNT(*) AS doc_count " +
                $"FROM `{tableName}`{wc} " +
                $"GROUP BY `{col}` " +
                $"ORDER BY doc_count DESC " +
                $"LIMIT {(spec.Size > 0 ? spec.Size : 10)}",

            SrAggKind.DateHistogram =>
                $"SELECT {DateBucketExpr(col, spec.CalendarInterval)} AS bucket_key, " +
                $"COUNT(*) AS doc_count " +
                $"FROM `{tableName}`{wc} " +
                $"GROUP BY bucket_key ORDER BY bucket_key",

            SrAggKind.Range => BuildRangeSql(tableName, col, spec.RangeBuckets, wc),
            SrAggKind.Avg   => $"SELECT AVG(`{col}`) AS metric_val FROM `{tableName}`{wc}",
            SrAggKind.Sum   => $"SELECT SUM(`{col}`) AS metric_val FROM `{tableName}`{wc}",
            SrAggKind.Min   => $"SELECT MIN(`{col}`) AS metric_val FROM `{tableName}`{wc}",
            SrAggKind.Max   => $"SELECT MAX(`{col}`) AS metric_val FROM `{tableName}`{wc}",
            SrAggKind.Count => $"SELECT COUNT(DISTINCT `{col}`) AS metric_val FROM `{tableName}`{wc}",

            _ => throw new ArgumentOutOfRangeException(nameof(spec.Kind))
        };

        return (sql, param);
    }

    internal static string BuildWhere(
        SchemaDescriptor schema,
        IEnumerable<SearchClause>? clauses,
        SearchLogic logic,
        DynamicParameters param,
        out int nextIdx)
    {
        nextIdx = 0;
        if (clauses is null) return "";

        var parts = new List<string>();

        foreach (var clause in clauses)
        {
            if (clause.Operator == SearchOperator.VectorSimilar) continue;

            var col = ResolveColumn(schema, clause.Property);
            if (col is null) continue;

            var pName = $"p{nextIdx++}";

            var condition = clause.Operator switch
            {
                SearchOperator.Equals => BuildEq(col, pName, clause.Value, param),
                SearchOperator.NotEquals =>
                    Condition($"`{col}` <> @{pName}", pName, GetScalarValue(clause.Value), param),
                SearchOperator.Contains =>
                    Condition($"`{col}` LIKE @{pName}", pName, $"%{clause.Value?.StringVal}%", param),
                SearchOperator.StartsWith =>
                    Condition($"`{col}` LIKE @{pName}", pName, $"{clause.Value?.StringVal}%", param),
                SearchOperator.EndsWith =>
                    Condition($"`{col}` LIKE @{pName}", pName, $"%{clause.Value?.StringVal}", param),
                SearchOperator.GreaterThan =>
                    Condition($"`{col}` > @{pName}", pName, GetScalarValue(clause.Value), param),
                SearchOperator.GreaterThanOrEquals =>
                    Condition($"`{col}` >= @{pName}", pName, GetScalarValue(clause.Value), param),
                SearchOperator.LessThan =>
                    Condition($"`{col}` < @{pName}", pName, GetScalarValue(clause.Value), param),
                SearchOperator.LessThanOrEquals =>
                    Condition($"`{col}` <= @{pName}", pName, GetScalarValue(clause.Value), param),
                SearchOperator.In => BuildIn(col, pName, clause.Value, param),
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
    /// Builds a FROM clause with one or more JOINs from a list of <see cref="JoinSpec"/>s,
    /// resolving each side's type against the <see cref="SchemaRegistry"/>. Populates
    /// <paramref name="tableMap"/> with every type name (primary + joined) mapped to its
    /// resolved <see cref="JoinContext"/> so callers can later qualify columns per-table.
    /// </summary>
    internal static string BuildFromWithJoins(
        string primaryTable,
        IReadOnlyList<JoinSpec> joins,
        SchemaRegistry registry,
        out IReadOnlyDictionary<string, JoinContext> tableMap)
    {
        var map = new Dictionary<string, JoinContext>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder($"FROM `{primaryTable}`");

        if (joins.Count == 0)
        {
            tableMap = map;
            return sb.ToString();
        }

        // Seed the map with the primary table's schema, keyed by left_type of the
        // first join (the primary table is always the left side of the join chain).
        var primarySchema = registry.Get(joins[0].LeftType)
            ?? throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Unknown type '{joins[0].LeftType}' referenced in join."));
        map[joins[0].LeftType] = new JoinContext(primaryTable, primarySchema, primaryTable);

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

    private static string BuildRangeSql(
        string tableName, string col,
        IReadOnlyList<SrRangeSpec>? buckets, string wc)
    {
        if (buckets is null || buckets.Count == 0)
            return $"SELECT NULL AS bucket_key, COUNT(*) AS doc_count FROM `{tableName}`{wc}";

        var cases = buckets.Select(b =>
        {
            var key = EscapeSqlString(b.Key);
            if (b.From is null && b.To is not null)
                return $"WHEN `{col}` < {b.To.Value} THEN '{key}'";
            if (b.From is not null && b.To is null)
                return $"WHEN `{col}` >= {b.From.Value} THEN '{key}'";
            if (b.From is not null && b.To is not null)
                return $"WHEN `{col}` >= {b.From.Value} AND `{col}` < {b.To.Value} THEN '{key}'";
            return null;
        }).OfType<string>();

        return $"SELECT CASE {string.Join(" ", cases)} END AS bucket_key, " +
               $"COUNT(*) AS doc_count FROM `{tableName}`{wc} GROUP BY bucket_key";
    }

    private static string? BuildEq(string col, string pName, SearchValue? val, DynamicParameters param)
    {
        param.Add(pName, GetScalarValue(val));
        return $"`{col}` = @{pName}";
    }

    private static string? BuildIn(string col, string pName, SearchValue? val, DynamicParameters param)
    {
        var list = val?.StringList?.Values.ToList() ?? [];
        if (list.Count == 0) return null;
        param.Add(pName, list);
        return $"`{col}` IN @{pName}";
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
    private static string DateBucketExpr(string col, string? interval) =>
        interval?.ToLowerInvariant() == "quarter"
            ? $"CONCAT(YEAR(`{col}`), '-Q', QUARTER(`{col}`))"
            : $"DATE_FORMAT(`{col}`, '{DateFormatFor(interval)}')";

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
}
