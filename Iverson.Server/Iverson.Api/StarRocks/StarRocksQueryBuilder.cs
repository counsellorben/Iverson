using System.Text;
using Dapper;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.StarRocks;

using SrAggKind  = Iverson.StarRocks.AggregationKind;
using SrAggSpec  = Iverson.StarRocks.AggregationDescriptor;
using SrRangeSpec = Iverson.StarRocks.RangeBucketDescriptor;

namespace Iverson.Api.StarRocks;

internal static class StarRocksQueryBuilder
{
    internal static (string Sql, DynamicParameters Param) BuildSearch(
        string tableName,
        SchemaDescriptor schema,
        SearchQuery? query,
        int page,
        int pageSize)
    {
        var param = new DynamicParameters();
        var where = BuildWhere(schema, query?.Clauses, query?.Logic ?? SearchLogic.And, param, out _);
        var order = BuildOrder(schema, query?.Sort);

        var limit  = pageSize > 0 ? pageSize : 50;
        var offset = page > 0 ? page * limit : 0;

        var sb = new StringBuilder($"SELECT * FROM `{tableName}`");
        if (where.Length > 0) sb.Append($" WHERE {where}");
        if (order.Length > 0) sb.Append($" ORDER BY {order}");
        sb.Append($" LIMIT {limit} OFFSET {offset}");

        return (sb.ToString(), param);
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
                $"SELECT DATE_FORMAT(`{col}`, '{DateFormatFor(spec.CalendarInterval)}') AS bucket_key, " +
                $"COUNT(*) AS doc_count " +
                $"FROM `{tableName}`{wc} " +
                $"GROUP BY bucket_key ORDER BY bucket_key",

            SrAggKind.Range => BuildRangeSql(tableName, col, spec.RangeBuckets, wc),

            SrAggKind.Avg         => $"SELECT AVG(`{col}`) AS metric_val FROM `{tableName}`{wc}",
            SrAggKind.Sum         => $"SELECT SUM(`{col}`) AS metric_val FROM `{tableName}`{wc}",
            SrAggKind.Min         => $"SELECT MIN(`{col}`) AS metric_val FROM `{tableName}`{wc}",
            SrAggKind.Max         => $"SELECT MAX(`{col}`) AS metric_val FROM `{tableName}`{wc}",
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
        var candidates = schema.ScalarColumns.Select(c => c.Name).Append(schema.KeyColumn.Name);
        return candidates.FirstOrDefault(c => string.Equals(c, property, StringComparison.OrdinalIgnoreCase));
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
            if (b.From is null && b.To is not null)
                return $"WHEN `{col}` < {b.To.Value} THEN '{b.Key}'";
            if (b.From is not null && b.To is null)
                return $"WHEN `{col}` >= {b.From.Value} THEN '{b.Key}'";
            if (b.From is not null && b.To is not null)
                return $"WHEN `{col}` >= {b.From.Value} AND `{col}` < {b.To.Value} THEN '{b.Key}'";
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

    private static string DateFormatFor(string? interval) => interval?.ToLowerInvariant() switch
    {
        "minute"  => "%Y-%m-%d %H:%i",
        "hour"    => "%Y-%m-%d %H",
        "day"     => "%Y-%m-%d",
        "week"    => "%Y-%u",
        "quarter" => "%Y-%m",
        "year"    => "%Y",
        _         => "%Y-%m"
    };
}
