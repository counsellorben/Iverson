using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Iverson.Client.Contracts;


namespace Iverson.StarRocks;

/// <summary>
/// A single table participating in a joined query: its physical table name,
/// schema descriptor, and the alias used to qualify columns in generated SQL.
/// </summary>
internal sealed record JoinContext(string TableName, StarRocksQuerySchema Schema, string Alias);

internal static class StarRocksQueryBuilder
{
    private static readonly ConditionalWeakTable<
        StarRocksQuerySchema,
        Dictionary<string, string>> _columnCache = new();
    internal static (string Sql, DynamicParameters Param) BuildSearch(
        string tableName,
        StarRocksQuerySchema schema,
        SearchQuery? query,
        int page,
        int pageSize,
        IReadOnlyList<string>? fields = null,
        IReadOnlyList<JoinSpec>? joins = null,
        Func<string, StarRocksQuerySchema?>? registry = null,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null,
        string? tenantDatabase = null)
    {
        var param = new DynamicParameters();

        var limit  = pageSize > 0 ? pageSize : 50;
        var offset = page > 0 ? page * limit : 0;

        string from;
        string where;
        string selectCols;
        string order;
        if (joins is { Count: > 0 })
        {
            from = BuildFromWithJoins(schema, joins, registry!, param, out var tableMap, authz, tenantDatabase);
            where = BuildWhere(schema, query?.Clauses, query?.Logic ?? SearchLogic.And, param, out _, tableMap, authz);

            // The primary table's own columns must be qualified with its alias once a JOIN is
            // in play — an unqualified `Id` (or any other column name shared with a joined
            // table, e.g. every table's key column) is ambiguous SQL and StarRocks rejects the
            // query at analysis time. BuildAggregate already qualifies its SELECT expressions
            // this way (see its local Quote/Resolve helpers above); BuildSelectColumns needs the
            // same treatment for BuildSearch's plain column list.
            var primaryAlias = tableMap[schema.TypeName].Alias;
            selectCols = BuildSelectColumns(schema, fields, primaryAlias);

            // ORDER BY must be resolved and field-gated against the same tableMap as WHERE —
            // see BuildOrder's remarks for why an unrestricted sort would otherwise leak a
            // restricted field's relative ordering (a side channel WHERE-clause gating alone
            // does not close).
            order = BuildOrder(schema, query?.Sort, tableMap, authz);

            if (authz is not null && authz.TryGetValue(schema.TypeName, out var primaryConstraint) && primaryConstraint.OwnerColumn is not null)
            {
                var ownerPredicate = $"`{primaryAlias}`.`{primaryConstraint.OwnerColumn}` = @__ownerVal";
                param.Add("__ownerVal", primaryConstraint.OwnerValue);
                where = where.Length > 0 ? $"({where}) AND {ownerPredicate}" : ownerPredicate;
            }

            // Tenant boundary is additive and unconditional — gated only on TenantColumn being
            // present, independently of the ownership block above (which is skipped for
            // CanReadAll callers). Every constraint is guaranteed to carry a tenant column now,
            // so this always fires for a registered type.
            if (authz is not null && authz.TryGetValue(schema.TypeName, out var tenantConstraint) && tenantConstraint.TenantColumn is not null)
            {
                var tenantPredicate = $"`{primaryAlias}`.`{tenantConstraint.TenantColumn}` = @__tenantVal";
                param.Add("__tenantVal", tenantConstraint.TenantValue);
                where = where.Length > 0 ? $"({where}) AND {tenantPredicate}" : tenantPredicate;
            }
        }
        else
        {
            from = $"FROM {TenantIdentifier.Qualify(tenantDatabase, tableName)}";
            where = BuildWhere(schema, query?.Clauses, query?.Logic ?? SearchLogic.And, param, out _, null, authz);
            selectCols = BuildSelectColumns(schema, fields);
            order = BuildOrder(schema, query?.Sort, null, authz);

            if (authz is not null && authz.TryGetValue(schema.TypeName, out var primaryConstraint) && primaryConstraint.OwnerColumn is not null)
            {
                var ownerPredicate = $"`{primaryConstraint.OwnerColumn}` = @__ownerVal";
                param.Add("__ownerVal", primaryConstraint.OwnerValue);
                where = where.Length > 0 ? $"({where}) AND {ownerPredicate}" : ownerPredicate;
            }

            // Tenant boundary is additive and unconditional — gated only on TenantColumn being
            // present, independently of the ownership block above (which is skipped for
            // CanReadAll callers).
            if (authz is not null && authz.TryGetValue(schema.TypeName, out var tenantConstraint) && tenantConstraint.TenantColumn is not null)
            {
                var tenantPredicate = $"`{tenantConstraint.TenantColumn}` = @__tenantVal";
                param.Add("__tenantVal", tenantConstraint.TenantValue);
                where = where.Length > 0 ? $"({where}) AND {tenantPredicate}" : tenantPredicate;
            }
        }

        var sb = new StringBuilder($"SELECT {selectCols} {from}");
        if (where.Length > 0) sb.Append($" WHERE {where}");
        if (order.Length > 0) sb.Append($" ORDER BY {order}");
        sb.Append($" LIMIT {limit} OFFSET {offset}");

        return (sb.ToString(), param);
    }

    private static string BuildSelectColumns(StarRocksQuerySchema schema, IReadOnlyList<string>? fields, string? primaryAlias = null)
    {
        string Quote(string name) => primaryAlias is null ? $"`{name}`" : $"`{primaryAlias}`.`{name}`";

        if (fields is null || fields.Count == 0)
        {
            var all = schema.ColumnNames
                .Select(Quote)
                .Prepend(Quote(schema.KeyColumnName));
            return string.Join(", ", all);
        }

        var resolved = new List<string> { schema.KeyColumnName };
        foreach (var f in fields)
        {
            var col = ResolveColumn(schema, f);
            if (col is not null && !col.Equals(schema.KeyColumnName, StringComparison.OrdinalIgnoreCase))
                resolved.Add(col);
        }
        return string.Join(", ", resolved.Select(Quote));
    }

    internal static (string Sql, DynamicParameters Param) BuildAggregate(
        string tableName,
        StarRocksQuerySchema schema,
        SearchQuery? query,
        AggregationDescriptor spec,
        SearchQuery? having = null,
        IReadOnlyList<JoinSpec>? joins = null,
        Func<string, StarRocksQuerySchema?>? registry = null,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null,
        string? tenantDatabase = null)
    {
        var param = new DynamicParameters();

        string from;
        IReadOnlyDictionary<string, JoinContext>? tableMap;
        if (joins is { Count: > 0 })
        {
            // Joined-type ownership predicates are appended to each JOIN's own ON clause inside
            // BuildFromWithJoins (never the outer WHERE — see that method's remarks for why).
            from = BuildFromWithJoins(schema, joins, registry!, param, out var tm, authz, tenantDatabase);
            tableMap = tm;
        }
        else
        {
            from = $"FROM {TenantIdentifier.Qualify(tenantDatabase, tableName)}";
            tableMap = null;
        }

        // Resolves a field against the joined tableMap when joins are present, otherwise
        // against the primary schema alone — mirroring the same tableMap-or-not split used
        // throughout BuildSearch/BuildGroupBy.
        string Resolve(string f) =>
            (tableMap is not null ? ResolveColumn(tableMap, f) : ResolveColumn(schema, f)) ?? f;

        // Same tableMap-or-not resolution split as Resolve above, but WITHOUT its "?? f"
        // fallback: used only by the authorization check below, which must be able to tell an
        // unresolvable field apart from a resolved-but-disallowed one. Passing an unresolved raw
        // property straight into IsFieldAllowed would risk its tableMap!.Values.First(...) throwing
        // an unhandled InvalidOperationException for a dotted-but-unmatched "Type.Field" string.
        string? ResolveStrict(string f) =>
            tableMap is not null ? ResolveColumn(tableMap, f) : ResolveColumn(schema, f);

        // Quotes an already-resolved column: two separately-backtick-quoted "alias"."field"
        // parts when joined (see QuoteQualified's doc comment for why a single backtick pair
        // around "alias.field" is invalid SQL), or a bare single-backtick pair otherwise.
        string Quote(string c) => tableMap is not null ? QuoteQualified(c) : $"`{c}`";

        // Field reject-on-reference: unlike Search's post-fetch masking, an aggregate over a
        // disallowed field would still leak its distribution through bucket keys or metric
        // values, so any reference to one — via spec.Field, spec.GroupByFields, or
        // spec.Expression — throws instead. spec.Field and spec.Expression are checked
        // independently (NOT else-if): AggregationDescriptor/AggregationSpec has no mutual
        // exclusivity between the two, and SQL generation for Terms/DateHistogram/Range always
        // uses spec.Field (via Resolve/col below) regardless of whether spec.Expression is also
        // set — only Avg/Sum/Min/Max/Count fall back to spec.Expression ?? Quote(col). Checking
        // only whichever one happened to be "primary" would let a caller smuggle a disallowed
        // Field past validation by also setting an innocuous, allowed Expression.
        void CheckFieldAllowed(string field)
        {
            var resolved = ResolveStrict(field)
                ?? throw new StarRocksQueryTranslationException($"Unknown aggregation field '{field}'.");
            if (!IsFieldAllowed(resolved, schema, tableMap, authz, out var typeName))
                throw new StarRocksQueryTranslationException(
                    $"Aggregation field '{field}' on '{typeName}' is not authorized for this caller.");
        }

        if (!string.IsNullOrEmpty(spec.Field))
        {
            CheckFieldAllowed(spec.Field);
        }
        if (!string.IsNullOrEmpty(spec.Expression))
        {
            StarRocksPipelineBuilder.RejectForbiddenCharacters(spec.Expression, $"Aggregation '{spec.Name}' expression");
            foreach (Match m in StarRocksPipelineBuilder.TokenRx.Matches(spec.Expression))
            {
                if (StarRocksPipelineBuilder.DeriveWhitelist.Contains(m.Value)) continue;
                CheckFieldAllowed(m.Value);
            }
        }

        if (spec.GroupByFields is { Count: > 0 })
            foreach (var f in spec.GroupByFields)
                CheckFieldAllowed(f);

        var where = tableMap is not null
            ? BuildWhere(schema, query?.Clauses, query?.Logic ?? SearchLogic.And, param, out _, tableMap, authz)
            : BuildWhere(schema, query?.Clauses, query?.Logic ?? SearchLogic.And, param, out _, null, authz);

        // Primary-type row-ownership: same wrap-and-AND predicate BuildSearch appends to its own
        // `where`, qualified with the primary alias when joined.
        if (authz is not null && authz.TryGetValue(schema.TypeName, out var primaryConstraint) && primaryConstraint.OwnerColumn is not null)
        {
            var ownerPredicate = tableMap is not null
                ? $"`{tableMap[schema.TypeName].Alias}`.`{primaryConstraint.OwnerColumn}` = @__ownerVal"
                : $"`{primaryConstraint.OwnerColumn}` = @__ownerVal";
            param.Add("__ownerVal", primaryConstraint.OwnerValue);
            where = where.Length > 0 ? $"({where}) AND {ownerPredicate}" : ownerPredicate;
        }

        // Tenant boundary is additive and unconditional — gated only on TenantColumn being
        // present, independently of the ownership block above (which is skipped for CanReadAll
        // callers).
        if (authz is not null && authz.TryGetValue(schema.TypeName, out var tenantConstraint) && tenantConstraint.TenantColumn is not null)
        {
            var tenantPredicate = tableMap is not null
                ? $"`{tableMap[schema.TypeName].Alias}`.`{tenantConstraint.TenantColumn}` = @__tenantVal"
                : $"`{tenantConstraint.TenantColumn}` = @__tenantVal";
            param.Add("__tenantVal", tenantConstraint.TenantValue);
            where = where.Length > 0 ? $"({where}) AND {tenantPredicate}" : tenantPredicate;
        }

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
            AggregationKind.Terms => groupCols is not null
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

            AggregationKind.DateHistogram =>
                $"SELECT {DateBucketExpr(Quote(col), spec.CalendarInterval)} AS bucket_key, " +
                $"COUNT(*) AS doc_count " +
                $"{from}{wc} " +
                $"GROUP BY bucket_key{hc} ORDER BY bucket_key",

            AggregationKind.Range => BuildRangeSql(from, Quote(col), spec.RangeBuckets, wc, hc),

            // spec.Expression is a client-settable field on the public AggregateRequest proto
            // contract (object_search.proto's AggregationSpec.expression) — it IS reachable by
            // any caller with read access to this type via the Aggregate RPC. Validated above via
            // RejectForbiddenCharacters + the TokenRx/DeriveWhitelist identifier allow-list before
            // being spliced into the aggregate function; do not weaken either check based on an
            // assumption that this field is trusted or server-only.
            AggregationKind.Avg   => $"SELECT AVG({spec.Expression ?? Quote(col)}) AS metric_val {from}{wc}{hc}",
            AggregationKind.Sum   => $"SELECT SUM({spec.Expression ?? Quote(col)}) AS metric_val {from}{wc}{hc}",
            AggregationKind.Min   => $"SELECT MIN({spec.Expression ?? Quote(col)}) AS metric_val {from}{wc}{hc}",
            AggregationKind.Max   => $"SELECT MAX({spec.Expression ?? Quote(col)}) AS metric_val {from}{wc}{hc}",
            AggregationKind.Count => $"SELECT COUNT(DISTINCT {spec.Expression ?? Quote(col)}) AS metric_val {from}{wc}{hc}",

            _ => throw new ArgumentOutOfRangeException(nameof(spec.Kind))
        };

        return (sql, param);
    }

    /// <summary>
    /// Builds a single compound SELECT that computes multiple metrics over one GROUP BY in a
    /// single SQL round-trip (e.g. TPC-H Q1: several SUM/AVG/COUNT columns, grouped by 2 keys,
    /// ordered, HAVING-filtered). Unlike <see cref="BuildAggregate"/>, which issues one SQL
    /// query per <see cref="AggregationDescriptor"/>, this emits one query for the whole
    /// <see cref="GroupByRequest"/>.
    /// </summary>
    internal static (string Sql, DynamicParameters Param) BuildGroupBy(
        string primaryTable,
        StarRocksQuerySchema schema,
        GroupByRequest request,
        Func<string, StarRocksQuerySchema?> registry,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null,
        string? tenantDatabase = null)
    {
        var param = new DynamicParameters();
        // Joined-type ownership predicates are appended to each JOIN's own ON clause inside
        // BuildFromWithJoins (never the outer WHERE — see that method's remarks for why).
        var from = BuildFromWithJoins(schema, request.Joins, registry, param, out var tableMap, authz, tenantDatabase);
        var where = BuildWhere(schema, request.Query?.Clauses, request.Query?.Logic ?? SearchLogic.And, param, out _, tableMap, authz);

        // Primary-type row-ownership: same wrap-and-AND predicate BuildSearch/BuildAggregate
        // append to their own `where`, qualified with the primary alias (BuildGroupBy always
        // has a tableMap — even with zero joins — so this is unconditional, unlike BuildSearch's
        // join/no-join split).
        if (authz is not null && authz.TryGetValue(schema.TypeName, out var primaryConstraint) && primaryConstraint.OwnerColumn is not null)
        {
            var ownerPredicate = $"`{tableMap[schema.TypeName].Alias}`.`{primaryConstraint.OwnerColumn}` = @__ownerVal";
            param.Add("__ownerVal", primaryConstraint.OwnerValue);
            where = where.Length > 0 ? $"({where}) AND {ownerPredicate}" : ownerPredicate;
        }

        // Tenant boundary is additive and unconditional — gated only on TenantColumn being
        // present, independently of the ownership block above (which is skipped for CanReadAll
        // callers).
        if (authz is not null && authz.TryGetValue(schema.TypeName, out var tenantConstraint) && tenantConstraint.TenantColumn is not null)
        {
            var tenantPredicate = $"`{tableMap[schema.TypeName].Alias}`.`{tenantConstraint.TenantColumn}` = @__tenantVal";
            param.Add("__tenantVal", tenantConstraint.TenantValue);
            where = where.Length > 0 ? $"({where}) AND {tenantPredicate}" : tenantPredicate;
        }

        var wc = where.Length > 0 ? $" WHERE {where}" : "";

        // Field reject-on-reference: a GROUP BY key or metric over a disallowed field would
        // leak its distribution through bucket keys or metric values, so any reference to one
        // throws instead (mirrors BuildAggregate's spec.Field/spec.Expression/spec.GroupByFields
        // treatment in Task 3).
        var keyCols = request.Keys
            .Select(k =>
            {
                var resolved = ResolveColumn(tableMap, k)
                    ?? throw new StarRocksQueryTranslationException(
                        $"Unknown or ambiguous GROUP BY key '{k}'.");
                if (!IsFieldAllowed(resolved, schema, tableMap, authz, out var typeName))
                    throw new StarRocksQueryTranslationException(
                        $"GROUP BY key '{k}' on '{typeName}' is not authorized for this caller.");
                return resolved;
            })
            .ToList();

        var metricExprs = request.Metrics.Select(m => BuildMetricExpr(m, schema, tableMap, authz)).ToList();

        var selectCols = keyCols.Select(QuoteQualified)
            .Concat(metricExprs)
            .ToList();

        var havingSql = BuildHaving(request.Having?.Clauses, request.Having?.Logic ?? SearchLogic.And, param);
        var hc = havingSql.Length > 0 ? $" HAVING {havingSql}" : "";

        // Field reject-on-reference: an ORDER BY over a disallowed field would leak that field's
        // relative ordering across rows — a side channel WHERE-clause field gating alone does not
        // close (mirrors keyCols' treatment above, and BuildSearch/BuildOrder's equivalent gate).
        var orderSql = request.OrderBy
            .Select(s =>
            {
                var resolved = ResolveColumn(tableMap, s.Property)
                    ?? throw new StarRocksQueryTranslationException(
                        $"Unknown or ambiguous ORDER BY property '{s.Property}'.");
                if (!IsFieldAllowed(resolved, schema, tableMap, authz, out var typeName))
                    throw new StarRocksQueryTranslationException(
                        $"ORDER BY property '{s.Property}' on '{typeName}' is not authorized for this caller.");
                return (col: resolved, s.Descending);
            })
            .Select(x => $"{QuoteQualified(x.col)} {(x.Descending ? "DESC" : "ASC")}")
            .ToList();
        var oc = orderSql.Count > 0 ? $" ORDER BY {string.Join(", ", orderSql)}" : "";

        var limit = request.Limit > 0 ? request.Limit : 10_000;

        var sql = $"SELECT {string.Join(", ", selectCols)} {from}{wc} " +
                   $"GROUP BY {string.Join(", ", keyCols.Select(QuoteQualified))}{hc}{oc} " +
                   $"LIMIT {limit}";

        return (sql, param);
    }

    // metric.expression is a client-settable field on the public GroupByRequest proto contract
    // (object_search.proto's MetricSpec.expression) — it IS reachable by any caller with read
    // access to this type via the GroupBy RPC. Validated below via RejectForbiddenCharacters +
    // the TokenRx/DeriveWhitelist identifier allow-list, and subject to the same field
    // reject-on-reference check as metric.Field (see remarks ahead of that check for why the
    // two are validated independently); do not weaken any of these based on an assumption that
    // this field is trusted or server-only.
    private static string BuildMetricExpr(
        MetricSpec metric,
        StarRocksQuerySchema schema,
        IReadOnlyDictionary<string, JoinContext> tableMap,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz)
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
            _ => throw new StarRocksQueryTranslationException(
                $"Metric '{metric.Name}' has unsupported type '{metric.Type}'; GroupBy metrics must be AVG, SUM, MIN, MAX, or COUNT.")
        };

        // Field reject-on-reference: metric.Field and metric.Expression are checked
        // independently below (NOT else-if) even though the SQL-emission branches further
        // down use them mutually exclusively (Expression, when set, is preferred over Field
        // and Field's resolved column never reaches the emitted SQL text in that case).
        // MetricSpec has no mutual exclusivity between Field and Expression, so without an
        // independent check here a caller could reference a disallowed Field and simply pair
        // it with an innocuous, allowed Expression to suppress the Field check entirely — the
        // exact shape of bug Task 3's review round found in BuildAggregate. Reject-on-reference
        // means any reference to a disallowed field is rejected, regardless of whether that
        // particular combination of Field+Expression would have actually spliced it into SQL.
        string? resolvedField = null;
        if (!string.IsNullOrEmpty(metric.Field))
        {
            resolvedField = ResolveColumn(tableMap, metric.Field)
                ?? throw new StarRocksQueryTranslationException(
                    $"Unknown or ambiguous field '{metric.Field}' referenced by metric '{metric.Name}'.");
            if (!IsFieldAllowed(resolvedField, schema, tableMap, authz, out var typeName))
                throw new StarRocksQueryTranslationException(
                    $"Field '{metric.Field}' on '{typeName}' referenced by metric '{metric.Name}' is not authorized for this caller.");
        }

        if (!string.IsNullOrEmpty(metric.Expression))
        {
            StarRocksPipelineBuilder.RejectForbiddenCharacters(metric.Expression, $"Metric '{metric.Name}' expression");
            foreach (Match m in StarRocksPipelineBuilder.TokenRx.Matches(metric.Expression))
            {
                if (StarRocksPipelineBuilder.DeriveWhitelist.Contains(m.Value)) continue;
                var resolvedToken = ResolveColumn(tableMap, m.Value)
                    ?? throw new StarRocksQueryTranslationException(
                        $"Metric '{metric.Name}' expression references unknown field '{m.Value}'.");
                if (!IsFieldAllowed(resolvedToken, schema, tableMap, authz, out var typeName))
                    throw new StarRocksQueryTranslationException(
                        $"Field '{m.Value}' on '{typeName}' referenced by metric '{metric.Name}' expression is not authorized for this caller.");
            }
        }

        if (!string.IsNullOrEmpty(metric.Expression))
            return $"{fn}({metric.Expression}) AS {quotedName}";

        if (resolvedField is not null)
            return $"{fn}({QuoteQualified(resolvedField)}) AS {quotedName}";

        // Both Field and Expression are empty/null. COUNT(*) is the one legitimate case for
        // this (handled above via isCountAll); every other metric kind requires a column
        // argument, so emitting the naive fallback here would produce invalid SQL like
        // "SUM(``)" — fail loudly instead.
        throw new StarRocksQueryTranslationException(
            $"metric '{metric.Name}' requires a field or expression");
    }

    internal static string BuildWhere(
        StarRocksQuerySchema schema,
        IEnumerable<SearchClause>? clauses,
        SearchLogic logic,
        DynamicParameters param,
        out int nextIdx,
        IReadOnlyDictionary<string, JoinContext>? tableMap = null,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
    {
        // Single quoting decision point: resolve + fully quote the column into one
        // ready-to-embed SQL identifier. Cross-schema (tableMap present) columns are
        // "alias.field" and MUST go through QuoteQualified (two separately-backtick-quoted
        // parts) — a single backtick pair around "alias.field" is invalid SQL (see
        // QuoteQualified's doc comment).
        Func<string, string?> resolve = tableMap is not null
            ? p =>
            {
                if (ResolveColumn(tableMap, p) is not { } qc) return null;
                if (!IsFieldAllowed(qc, schema, tableMap, authz, out var typeName))
                    throw new StarRocksQueryTranslationException($"Filter property '{p}' on '{typeName}' is not authorized for this caller.");
                return QuoteQualified(qc);
            }
            : p =>
            {
                if (ResolveColumn(schema, p) is not { } c) return null;
                if (!IsFieldAllowed(c, schema, null, authz, out var typeName))
                    throw new StarRocksQueryTranslationException($"Filter property '{p}' on '{typeName}' is not authorized for this caller.");
                return $"`{c}`";
            };
        return BuildWhere(resolve, clauses, logic, param, "p", out nextIdx);
    }

    /// <summary>
    /// Resolves a column's owning type and checks it against that type's <see cref="AuthorizationConstraint.AllowedFields"/>
    /// in one call. Shared by the <see cref="BuildWhere(StarRocksQuerySchema, IEnumerable{SearchClause}?, SearchLogic, DynamicParameters, out int, IReadOnlyDictionary{string, JoinContext}?, IReadOnlyDictionary{string, AuthorizationConstraint}?)"/>
    /// overload's own filter-clause check above, and directly by other call sites that need the
    /// same alias-to-type-name resolution (e.g. SELECT-column and JOIN-condition authorization
    /// checks) rather than reimplementing it.
    /// </summary>
    internal static bool IsFieldAllowed(
        string resolvedColumnOrAliasDotColumn,
        StarRocksQuerySchema schema,
        IReadOnlyDictionary<string, JoinContext>? tableMap,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz,
        out string typeName)
    {
        var dotIdx = tableMap is null ? -1 : resolvedColumnOrAliasDotColumn.LastIndexOf('.');
        typeName = dotIdx < 0
            ? schema.TypeName
            : tableMap!.Values.First(v => v.Alias == resolvedColumnOrAliasDotColumn[..dotIdx]).Schema.TypeName;
        var bareField = dotIdx < 0 ? resolvedColumnOrAliasDotColumn : resolvedColumnOrAliasDotColumn[(dotIdx + 1)..];
        return authz is null || !authz.TryGetValue(typeName, out var constraint) || constraint.AllowedFields is null
            || constraint.AllowedFields.Contains(bareField);
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
            if (clause.Operator == SearchOperator.VectorSimilar)
                throw new StarRocksQueryTranslationException(
                    "VECTOR_SIMILAR clauses are not supported by the SQL search path; " +
                    "use the SearchSimilar or SearchChunks RPCs for vector search.");

            var quotedCol = resolveQuoted(clause.Property);
            if (quotedCol is null) continue;

            var pName = $"{paramPrefix}{nextIdx++}";

            var condition = BuildCondition(quotedCol, pName, clause, param);

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
    /// Builds a HAVING clause from the same clause-matching logic as <see cref="BuildWhere(StarRocksQuerySchema, IEnumerable{SearchClause}?, SearchLogic, DynamicParameters, out int, IReadOnlyDictionary{string, JoinContext}?)"/>,
    /// but without the schema-backed <see cref="ResolveColumn(StarRocksQuerySchema, string)"/> guard —
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
            if (clause.Operator == SearchOperator.VectorSimilar)
                throw new StarRocksQueryTranslationException(
                    "VECTOR_SIMILAR clauses are not supported by the SQL search path; " +
                    "use the SearchSimilar or SearchChunks RPCs for vector search.");

            var col = clause.Property;
            if (string.IsNullOrEmpty(col)) continue;
            var quotedCol = $"`{EscapeIdentifier(col)}`";

            var pName = $"{paramPrefix}{nextIdx++}";

            var condition = BuildCondition(quotedCol, pName, clause, param);

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

    internal static string? ResolveColumn(StarRocksQuerySchema schema, string property)
    {
        var index = _columnCache.GetValue(schema, static s =>
            s.ColumnNames
                .Append(s.KeyColumnName)
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
    /// resolving each side's type against the <see cref="Func{T, TResult}"/> registry lookup. Always populates
    /// <paramref name="tableMap"/> with at least the primary table (keyed by
    /// <paramref name="primarySchema"/>'s <c>TypeName</c>), plus every joined type name, each
    /// mapped to its resolved <see cref="JoinContext"/> so callers can later qualify columns
    /// per-table — including in the no-join case.
    /// </summary>
    /// <remarks>
    /// When <paramref name="authz"/> carries an ownership constraint for a joined type, that
    /// type's owner condition is appended to its own JOIN's <c>ON</c> clause — never spliced into
    /// the outer <c>WHERE</c>. For an INNER JOIN either placement is equivalent, but for LEFT/RIGHT/FULL
    /// a WHERE-clause condition on the joined side would silently collapse the join to INNER
    /// semantics (rows with no matching/authorized joined row would be dropped entirely instead of
    /// surfacing with NULLs on the joined side).
    /// </remarks>
    internal static string BuildFromWithJoins(
        StarRocksQuerySchema primarySchema,
        IReadOnlyList<JoinSpec> joins,
        Func<string, StarRocksQuerySchema?> registry,
        DynamicParameters param,
        out IReadOnlyDictionary<string, JoinContext> tableMap,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null,
        string? tenantDatabase = null)
    {
        var map = new Dictionary<string, JoinContext>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder($"FROM {TenantIdentifier.Qualify(tenantDatabase, primarySchema.TableName)}");

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
                var leftSchema = registry(join.LeftType)
                    ?? throw new StarRocksQueryTranslationException(
                        $"Unknown type '{join.LeftType}' referenced in join.");
                leftCtx = new JoinContext(leftSchema.TableName, leftSchema, leftSchema.TableName);
                map[join.LeftType] = leftCtx;
            }

            var rightSchema = registry(join.RightType)
                ?? throw new StarRocksQueryTranslationException(
                    $"Unknown type '{join.RightType}' referenced in join.");
            var rightCtx = new JoinContext(rightSchema.TableName, rightSchema, rightSchema.TableName);

            var leftCol = ResolveColumn(leftCtx.Schema, join.LeftField)
                ?? throw new StarRocksQueryTranslationException(
                    $"Unknown field '{join.LeftField}' on type '{join.LeftType}' referenced in join.");
            var rightCol = ResolveColumn(rightCtx.Schema, join.RightField)
                ?? throw new StarRocksQueryTranslationException(
                    $"Unknown field '{join.RightField}' on type '{join.RightType}' referenced in join.");

            var kind = join.Kind switch
            {
                JoinKind.Left  => "LEFT",
                JoinKind.Right => "RIGHT",
                JoinKind.Full  => "FULL",
                _              => "INNER"
            };

            var ownerCond = "";
            if (authz is not null && authz.TryGetValue(join.RightType, out var joinedConstraint) && joinedConstraint.OwnerColumn is not null)
            {
                var pName = $"__owner{map.Count}";
                param.Add(pName, joinedConstraint.OwnerValue);
                ownerCond = $" AND `{rightCtx.Alias}`.`{joinedConstraint.OwnerColumn}` = @{pName}";
            }

            // Tenant boundary is additive and unconditional — gated only on TenantColumn being
            // present, independently of the ownership block above. Every joined type is now
            // guaranteed to carry a tenant column, so a fixed parameter name would collide across
            // joins; mirror the owner predicate's per-join unique naming (map.Count) exactly.
            var tenantCond = "";
            if (authz is not null && authz.TryGetValue(join.RightType, out var joinedTenantConstraint) && joinedTenantConstraint.TenantColumn is not null)
            {
                var tName = $"__tenant{map.Count}";
                param.Add(tName, joinedTenantConstraint.TenantValue);
                tenantCond = $" AND `{rightCtx.Alias}`.`{joinedTenantConstraint.TenantColumn}` = @{tName}";
            }

            sb.Append(
                $" {kind} JOIN {TenantIdentifier.Qualify(tenantDatabase, rightCtx.TableName)} ON " +
                $"`{leftCtx.Alias}`.`{leftCol}` = `{rightCtx.Alias}`.`{rightCol}`{ownerCond}{tenantCond}");

            map[join.RightType] = rightCtx;
        }

        tableMap = map;
        return sb.ToString();
    }

    /// <summary>
    /// Builds an ORDER BY clause from <see cref="SearchQuery.Sort"/>. Unresolvable sort
    /// properties are silently skipped (matching the pre-existing behavior), but a
    /// resolved-but-disallowed field throws — an unrestricted sort would otherwise let a caller
    /// infer a restricted field's relative ordering across rows, a side channel that WHERE-clause
    /// field gating (<see cref="BuildWhere(StarRocksQuerySchema, IEnumerable{SearchClause}?, SearchLogic, DynamicParameters, out int, IReadOnlyDictionary{string, JoinContext}?, IReadOnlyDictionary{string, AuthorizationConstraint}?)"/>)
    /// alone does not close.
    /// </summary>
    private static string BuildOrder(
        StarRocksQuerySchema schema,
        IEnumerable<SearchSort>? sorts,
        IReadOnlyDictionary<string, JoinContext>? tableMap,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz)
    {
        if (sorts is null) return "";

        var parts = new List<string>();
        foreach (var s in sorts)
        {
            var resolved = tableMap is not null ? ResolveColumn(tableMap, s.Property) : ResolveColumn(schema, s.Property);
            if (resolved is null) continue;

            if (!IsFieldAllowed(resolved, schema, tableMap, authz, out var typeName))
                throw new StarRocksQueryTranslationException(
                    $"Sort property '{s.Property}' on '{typeName}' is not authorized for this caller.");

            var quoted = tableMap is not null ? QuoteQualified(resolved) : $"`{resolved}`";
            parts.Add($"{quoted} {(s.Descending ? "DESC" : "ASC")}");
        }
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
        IReadOnlyList<RangeBucketDescriptor>? buckets, string wc, string hc = "")
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
    /// Maps one <see cref="SearchClause"/> to a SQL condition fragment, given an already-resolved
    /// and fully-quoted column identifier. Shared by <see cref="BuildWhere(Func{string, string?}, IEnumerable{SearchClause}?, SearchLogic, DynamicParameters, string, out int)"/>
    /// and <see cref="BuildHaving"/> — the only difference between the two call sites is how
    /// <paramref name="quotedCol"/> was produced (schema/tableMap-resolved for WHERE, used
    /// verbatim for HAVING); the operator-to-SQL mapping itself is identical.
    /// </summary>
    private static string? BuildCondition(string quotedCol, string pName, SearchClause clause, DynamicParameters param) =>
        clause.Operator switch
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
