using Dapper;
using Iverson.Client.Contracts;

namespace Iverson.StarRocks;

/// <summary>
/// Compiles a MatchPattern TYPE_ROWS request into a single StarRocks <c>SELECT</c> (spec §3.2).
/// Every referenced column is validated against the caller's visible column set, and every
/// measure name against the ONE_ROW/ALL_ROWS output-column set (spec §1), before any SQL is
/// built — no store is touched until this returns.
/// </summary>
internal static class MatchRowsQueryBuilder
{
    internal static (string Sql, DynamicParameters Param) Build(
        EngagementQuerySchema schema, MatchRowsRequest request,
        IReadOnlyDictionary<string, AuthorizationConstraint> authz, string? tenantDatabase)
    {
        // 1. Visible set: the caller's authorized columns (ColumnsFor already excludes the
        // server-owned tenant column and any AllowedFields restriction), minus this type's bytes
        // columns — those can never be read, projected, partitioned, sorted, or filtered on by a
        // pattern. Compared OrdinalIgnoreCase throughout, matching the dictionary's own comparer.
        var constraint = authz.TryGetValue(schema.TypeName, out var c) ? c : null;
        var visible = StarRocksPipelineBuilder.ColumnsFor(schema, constraint);
        foreach (var excluded in request.ExcludedColumns)
            visible.Remove(excluded);

        // 2. Membership, in slot order, before any SQL is built.
        foreach (var name in request.PartitionBy) RequireVisible(visible, name, "partition_by");
        foreach (var sort in request.OrderBy) RequireVisible(visible, sort.Property, "order_by");
        foreach (var clause in request.Where) RequireVisible(visible, clause.Property, "where");
        foreach (var name in request.ReferencedColumns) RequireVisible(visible, name, "define/measures");

        // 3. Measure-name collision (spec §1). ONE_ROW's only output columns are the partition
        // columns; ALL_ROWS projects every visible column. A measure sharing an output column's
        // exact (Ordinal) spelling would collide with it in the result row.
        var outputColumns = request.OneRowPerMatch
            ? request.PartitionBy.Select(p => visible[p]).ToList()
            : visible.Values.ToList();
        foreach (var m in request.MeasureNames)
        {
            var collision = outputColumns.FirstOrDefault(o => string.Equals(o, m, StringComparison.Ordinal));
            if (collision is not null)
                throw new EngagementQueryTranslationException(
                    $"MatchPattern: measure name '{m}' collides with the output column '{collision}'.");
        }

        // 4. WHERE, then the owner and tenant predicates — exactly the composition
        // StarRocksQueryBuilder's own no-join read paths use (StarRocksQueryBuilder.cs:109-132).
        var param = new DynamicParameters();
        var where = StarRocksQueryBuilder.BuildWhere(
            schema, request.Where, request.WhereLogic, param, out _, null, authz);

        if (constraint?.OwnerColumn is not null)
        {
            var ownerPredicate = $"`{constraint.OwnerColumn}` = @__ownerVal";
            param.Add("__ownerVal", constraint.OwnerValue);
            where = where.Length > 0 ? $"({where}) AND {ownerPredicate}" : ownerPredicate;
        }

        if (constraint?.TenantColumn is not null)
        {
            var tenantPredicate = $"`{constraint.TenantColumn}` = @__tenantVal";
            param.Add("__tenantVal", constraint.TenantValue);
            where = where.Length > 0 ? $"({where}) AND {tenantPredicate}" : tenantPredicate;
        }

        // 5. Projection: the key, then the canonical columns once each, skipping the key.
        var projected = new List<string> { schema.KeyColumnName };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { schema.KeyColumnName };
        if (request.OneRowPerMatch)
        {
            foreach (var name in request.PartitionBy.Concat(request.ReferencedColumns))
            {
                var col = visible[name];
                if (seen.Add(col)) projected.Add(col);
            }
        }
        else
        {
            foreach (var col in visible.Values)
                if (seen.Add(col)) projected.Add(col);
        }
        var projection = string.Join(", ", projected.Select(p => $"`{p}`"));

        // 6. ORDER BY: the canonical partition columns, then each canonical sort column (DESC
        // when descending), then the key as the final tie-breaker.
        var orderParts = request.PartitionBy.Select(p => $"`{visible[p]}`")
            .Concat(request.OrderBy.Select(s => $"`{visible[s.Property]}`{(s.Descending ? " DESC" : "")}"))
            .Append($"`{schema.KeyColumnName}`");
        var orderBy = string.Join(", ", orderParts);

        // 7. SQL. Every identifier above came from the visible set; the LIMIT operand is an int.
        var sql =
            $"SELECT {projection} FROM {TenantIdentifier.Qualify(tenantDatabase, schema.TableName)}" +
            $"{(where.Length > 0 ? " WHERE " + where : "")} ORDER BY {orderBy} LIMIT {request.MaxRowsScanned + 1}";

        return (sql, param);
    }

    private static void RequireVisible(Dictionary<string, string> visible, string name, string slot)
    {
        if (!visible.ContainsKey(name))
            throw new EngagementQueryTranslationException(
                $"MatchPattern: unknown column '{name}' in {slot}.");
    }
}
