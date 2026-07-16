using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Iverson.Client.Contracts;

namespace Iverson.StarRocks;

/// <summary>
/// One pipeline step's output column set: <see cref="Columns"/> maps a referenced
/// name (case-insensitive) to the canonical output column name emitted in SQL.
/// </summary>
internal sealed record StepColumns(string Name, Dictionary<string, string> Columns);

/// <summary>
/// Compiles a <see cref="PipelineRequest"/> into a single StarRocks CTE-chain query.
/// Pass 1 (<see cref="TrackAndValidate"/>) computes every step's output column set and
/// rejects invalid references via <see cref="StarRocksQueryTranslationException"/> before any SQL is built.
/// </summary>
internal static class StarRocksPipelineBuilder
{
    internal const string BaseStepName = "base";

    private static readonly Regex IdentifierRx = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    internal static readonly Regex TokenRx      = new("[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

    // Identifiers a Derive expression may use besides input columns. Anything else —
    // including SELECT/FROM/WHERE, which blocks subqueries — fails validation.
    internal static readonly HashSet<string> DeriveWhitelist = new(StringComparer.OrdinalIgnoreCase)
    {
        "SUM", "AVG", "MIN", "MAX", "COUNT", "OVER", "PARTITION", "BY", "ORDER",
        "ASC", "DESC", "COALESCE", "NULLIF", "ROUND", "ABS", "AND", "OR", "NOT", "NULL"
    };

    // Column-introduction filtering (authorization Task 5, Step 1): when constraint carries a
    // non-null AllowedFields, any column name not in it is omitted from the returned dictionary
    // so it can never be referenced downstream (select/where/derive/join/etc.) — the key column
    // is never excluded, per IRowFieldAuthorizationEvaluator's existing contract (a caller always
    // sees the primary key even under field restriction).
    private static Dictionary<string, string> ColumnsFor(
        StarRocksQuerySchema schema, AuthorizationConstraint? constraint = null)
    {
        var cols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [schema.KeyColumnName] = schema.KeyColumnName
        };
        foreach (var c in schema.ColumnNames)
        {
            if (constraint?.AllowedFields is not null && !constraint.AllowedFields.Contains(c)) continue;
            cols[c] = c;
        }
        return cols;
    }

    internal static IReadOnlyList<StepColumns> TrackAndValidate(
        StarRocksQuerySchema schema,
        PipelineRequest request,
        Func<string, StarRocksQuerySchema?> registry,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
    {
        var steps = new List<StepColumns>();

        var primaryConstraint = authz is not null && authz.TryGetValue(schema.TypeName, out var pc) ? pc : null;
        var baseColumns = ColumnsFor(schema, primaryConstraint);
        steps.Add(new StepColumns(BaseStepName, baseColumns));

        foreach (var clause in request.BaseWhere)
            RequireColumn(BaseStepName, baseColumns, clause.Property);

        foreach (var step in request.Steps)
        {
            ValidateStepName(step, steps, registry);
            var input = ResolveInput(step, steps);
            var output = ValidateStepAndComputeOutput(step, input, steps, registry, authz);
            steps.Add(new StepColumns(step.Name, output));
        }

        // Final ORDER BY resolves against the last step's output.
        var last = steps[^1];
        foreach (var sort in request.OrderBy)
            RequireColumn(last.Name, last.Columns, sort.Property);

        return steps;
    }

    private static void ValidateStepName(
        PipelineStep step, List<StepColumns> earlier, Func<string, StarRocksQuerySchema?> registry)
    {
        if (string.IsNullOrEmpty(step.Name) || !IdentifierRx.IsMatch(step.Name))
            throw Invalid($"Step name '{step.Name}' is not a valid identifier.");
        if (step.Name.Equals(BaseStepName, StringComparison.OrdinalIgnoreCase))
            throw Invalid($"Step name '{step.Name}' is reserved for the implicit base step.");
        var duplicate = earlier.FirstOrDefault(
            s => s.Name.Equals(step.Name, StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null)
            throw Invalid($"Duplicate step name '{duplicate.Name}'.");
        if (registry(step.Name) is not null)
            throw Invalid($"Step name '{step.Name}' collides with a registered type name.");
    }

    private static StepColumns ResolveInput(PipelineStep step, List<StepColumns> earlier)
    {
        if (string.IsNullOrEmpty(step.Reads)) return earlier[^1];

        return earlier.FirstOrDefault(
                s => s.Name.Equals(step.Reads, StringComparison.OrdinalIgnoreCase))
            ?? throw Invalid(
                $"Step '{step.Name}': reads '{step.Reads}' does not name an earlier step.");
    }

    private static Dictionary<string, string> ValidateStepAndComputeOutput(
        PipelineStep step,
        StepColumns input,
        List<StepColumns> earlier,
        Func<string, StarRocksQuerySchema?> registry,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
    {
        var isAggregate = step.GroupBy.Count > 0 || step.Metrics.Count > 0 || step.Having.Count > 0;

        if (step.Windows.Count > 0 && isAggregate)
            throw Invalid($"Step '{step.Name}': window functions and GROUP BY/metrics/HAVING " +
                          "cannot share a step; put the window in the next step.");
        if (isAggregate && step.Select.Count > 0)
            throw Invalid($"Step '{step.Name}': select projection is not valid on an aggregate " +
                          "step; project in a following step.");
        if (step.Joins.Count > 0 && step.Select.Count == 0)
            throw Invalid($"Step '{step.Name}': a step with joins requires an explicit select " +
                          "projection to resolve column collisions.");
        if (step.Joins.Count > 0 && (step.Windows.Count > 0 || step.Derive.Count > 0))
            throw Invalid($"Step '{step.Name}': joins cannot be combined with window functions " +
                          "or derive expressions in the same step; window/derive expressions " +
                          "can only reference input-step columns, so put them in a following step.");
        if (isAggregate && step.GroupBy.Count == 0)
            throw Invalid($"Step '{step.Name}': metrics/HAVING require at least one GROUP BY key.");

        foreach (var clause in step.Where)
            RequireColumn(step.Name, input.Columns, clause.Property);

        // Join sources — resolution against prior steps or the schema registry.
        var joinSources = ResolveJoinSources(step, earlier, registry, authz);

        foreach (var join in step.Joins)
        {
            if (join.On.Count == 0)
                throw Invalid($"Step '{step.Name}': join to '{join.Source}' requires at least " +
                              "one ON condition.");
            var src = joinSources[join.Source];
            foreach (var cond in join.On)
            {
                RequireColumn(step.Name, input.Columns, cond.Left);
                RequireColumn(step.Name, src.Columns, cond.Right);
            }
        }

        var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void AddOutput(string alias)
        {
            if (!output.TryAdd(alias, alias))
                throw Invalid($"Step '{step.Name}': duplicate output alias '{alias}'.");
        }

        if (isAggregate)
        {
            foreach (var key in step.GroupBy)
            {
                RequireColumn(step.Name, input.Columns, key.Field);
                AddOutput(OutputNameFor(key, input.Columns));
            }
            foreach (var m in step.Metrics)
            {
                if (string.IsNullOrEmpty(m.Name) || !IdentifierRx.IsMatch(m.Name))
                    throw Invalid($"Step '{step.Name}': metric alias '{m.Name}' is not a valid identifier.");
                // Field and Expression are checked independently (NOT else-if) even though
                // EmitMetric's own SQL-emission prefers Expression over Field when both are set
                // (Field's resolved column then never reaches the emitted SQL text). MetricSpec
                // has no mutual exclusivity between the two, so without an independent check here
                // a caller could reference a disallowed column via Field and pair it with an
                // innocuous, allowed Expression to suppress the Field check entirely — the exact
                // shape of bypass Task 3's review round found in BuildAggregate, and the same
                // defense-in-depth Task 4 applied to BuildMetricExpr for this identical type.
                if (!string.IsNullOrEmpty(m.Field))
                    RequireColumn(step.Name, input.Columns, m.Field);
                else if (string.IsNullOrEmpty(m.Expression) && m.Type != AggregationType.Count)
                    throw Invalid($"Step '{step.Name}': metric '{m.Name}' requires a field or expression.");
                if (!string.IsNullOrEmpty(m.Expression))
                {
                    foreach (Match tok in TokenRx.Matches(m.Expression))
                    {
                        if (DeriveWhitelist.Contains(tok.Value)) continue;
                        if (!input.Columns.ContainsKey(tok.Value))
                            throw Invalid($"Step '{step.Name}': metric '{m.Name}' expression references " +
                                          $"'{tok.Value}', which is neither an input column nor a whitelisted function.");
                    }
                }
                AddOutput(m.Name);
            }
            var metricAliases = new Dictionary<string, string>(output, StringComparer.OrdinalIgnoreCase);
            foreach (var h in step.Having)
                RequireColumn(step.Name, metricAliases, h.Property);
            return output;
        }

        // Non-aggregate step.
        if (step.Select.Count > 0)
        {
            foreach (var item in step.Select)
            {
                var source = ResolveSelectSource(step, item, input, joinSources);
                if (item.All)
                {
                    // "all: true" scoping (Step 2): a fresh join target (a registered type,
                    // resolvable via the registry — NOT a prior step; step outputs are already
                    // filtered per Step 1) whose constraint restricts AllowedFields cannot be
                    // wildcard-expanded — that would either silently narrow the result to fewer
                    // columns than the caller asked for, or (via EmitSelectItem's `alias.*`
                    // emission) leak the source's raw physical columns past the restriction.
                    // Fail loudly instead of guessing. A prior-step source needs no such check:
                    // its Columns dictionary already only contains allowed names.
                    var isFreshRestrictedType =
                        registry(source.Name) is not null &&
                        earlier.All(s => !s.Name.Equals(source.Name, StringComparison.OrdinalIgnoreCase)) &&
                        authz is not null && authz.TryGetValue(source.Name, out var srcConstraint) &&
                        srcConstraint.AllowedFields is not null;
                    if (isFreshRestrictedType)
                        throw Invalid($"Step '{step.Name}': 'all: true' against '{source.Name}' is not " +
                                      "permitted for this caller (restricted field set); select individual columns instead.");
                    foreach (var col in source.Columns.Values) AddOutput(col);
                }
                else
                {
                    RequireColumn(step.Name, source.Columns, item.Column);
                    if (!string.IsNullOrEmpty(item.Alias) && !IdentifierRx.IsMatch(item.Alias))
                        throw Invalid($"Step '{step.Name}': select alias '{item.Alias}' is not a valid identifier.");
                    AddOutput(string.IsNullOrEmpty(item.Alias) ? source.Columns[item.Column] : item.Alias);
                }
            }
        }
        else
        {
            foreach (var col in input.Columns.Values) AddOutput(col);
        }

        foreach (var w in step.Windows)
        {
            ValidateWindow(step.Name, w, input.Columns);
            AddOutput(w.Alias);
        }

        // Derive sees input columns plus window aliases already added this step.
        var deriveScope = new Dictionary<string, string>(output, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in input.Columns) deriveScope.TryAdd(kv.Key, kv.Value);
        foreach (var d in step.Derive)
        {
            if (string.IsNullOrEmpty(d.Alias) || !IdentifierRx.IsMatch(d.Alias))
                throw Invalid($"Step '{step.Name}': derive alias '{d.Alias}' is not a valid identifier.");
            ValidateDeriveExpr(step.Name, d, deriveScope);
            AddOutput(d.Alias);
            deriveScope.TryAdd(d.Alias, d.Alias);
        }

        return output;
    }

    private static Dictionary<string, StepColumns> ResolveJoinSources(
        PipelineStep step, List<StepColumns> earlier, Func<string, StarRocksQuerySchema?> registry,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
    {
        var sources = new Dictionary<string, StepColumns>(StringComparer.OrdinalIgnoreCase);
        foreach (var join in step.Joins)
        {
            var prior = earlier.FirstOrDefault(
                s => s.Name.Equals(join.Source, StringComparison.OrdinalIgnoreCase));
            if (prior is not null)
            {
                sources[prior.Name] = prior;
                continue;
            }

            var joinedSchema = registry(join.Source)
                ?? throw Invalid($"Step '{step.Name}': join source '{join.Source}' is neither " +
                                 "an earlier step nor a registered type.");
            var constraint = authz is not null && authz.TryGetValue(joinedSchema.TypeName, out var c) ? c : null;
            sources[joinedSchema.TypeName] = new StepColumns(joinedSchema.TypeName, ColumnsFor(joinedSchema, constraint));
        }
        return sources;
    }

    private static StepColumns ResolveSelectSource(
        PipelineStep step, SelectItem item, StepColumns input,
        Dictionary<string, StepColumns> joinSources)
    {
        if (string.IsNullOrEmpty(item.Source) ||
            item.Source.Equals(input.Name, StringComparison.OrdinalIgnoreCase))
            return input;

        return joinSources.TryGetValue(item.Source, out var src)
            ? src
            : throw Invalid($"Step '{step.Name}': select source '{item.Source}' is neither the " +
                            "step's input nor one of its join sources.");
    }

    private static void ValidateWindow(
        string stepName, WindowFunction w, Dictionary<string, string> input)
    {
        if (string.IsNullOrEmpty(w.Alias) || !IdentifierRx.IsMatch(w.Alias))
            throw Invalid($"Step '{stepName}': window alias '{w.Alias}' is not a valid identifier.");
        if (string.IsNullOrEmpty(w.OrderBy))
            throw Invalid($"Step '{stepName}': window '{w.Alias}' requires order_by.");
        RequireColumn(stepName, input, w.OrderBy);
        if (!string.IsNullOrEmpty(w.PartitionBy))
            RequireColumn(stepName, input, w.PartitionBy);

        var needsField = w.Kind is WindowFunctionKind.RunningSum or WindowFunctionKind.RunningAvg
            or WindowFunctionKind.Lag or WindowFunctionKind.Lead;
        if (needsField && string.IsNullOrEmpty(w.Field))
            throw Invalid($"Step '{stepName}': window '{w.Alias}' ({w.Kind}) requires a field.");
        if (!string.IsNullOrEmpty(w.Field))
            RequireColumn(stepName, input, w.Field);
    }

    /// <summary>
    /// Rejects a raw SQL fragment that contains a semicolon, quote, backtick, or SQL comment
    /// sequence — characters that a token-shaped identifier allow-list (<see cref="TokenRx"/>/
    /// <see cref="DeriveWhitelist"/>) alone would never inspect, since none of them ever match
    /// an identifier pattern in the first place. Shared by every raw-expression field spliced
    /// into generated SQL (<see cref="ValidateDeriveExpr"/> below, and
    /// <c>StarRocksQueryBuilder.BuildAggregate</c>/<c>BuildMetricExpr</c>) so all such fields
    /// get the same defense-in-depth denylist, not just whichever one a reviewer happened to
    /// look at most recently.
    /// </summary>
    internal static void RejectForbiddenCharacters(string expr, string errorContext)
    {
        if (expr.Contains(';') || expr.Contains('\'') || expr.Contains('`') ||
            expr.Contains("--") || expr.Contains("/*") || expr.Contains("*/"))
            throw Invalid($"{errorContext} contains a forbidden character " +
                          "(no semicolons, quotes, backticks, or SQL comment sequences).");
    }

    private static void ValidateDeriveExpr(
        string stepName, DeriveColumn d, Dictionary<string, string> available)
    {
        RejectForbiddenCharacters(d.Expr, $"Step '{stepName}': derive '{d.Alias}'");
        foreach (Match m in TokenRx.Matches(d.Expr))
        {
            if (DeriveWhitelist.Contains(m.Value)) continue;
            if (available.ContainsKey(m.Value)) continue;
            throw Invalid($"Step '{stepName}': derive '{d.Alias}' references '{m.Value}', which is " +
                          "neither an input column nor a whitelisted function.");
        }
    }

    internal static string OutputNameFor(GroupKey key, Dictionary<string, string> input) =>
        key.DateTrunc == DateTrunc.None
            ? input[key.Field]
            : $"{input[key.Field]}_{key.DateTrunc.ToString().ToLowerInvariant()}";

    private static void RequireColumn(
        string stepName, Dictionary<string, string> columns, string property)
    {
        if (string.IsNullOrEmpty(property) || !columns.ContainsKey(property))
            throw Invalid($"Step '{stepName}': unknown column or alias '{property}'.");
    }

    private static StarRocksQueryTranslationException Invalid(string message) =>
        new(message);

    /// <summary>
    /// Compiles the request into one SQL statement. <c>LastCols</c> is the final step's tracked
    /// (already field-restriction-filtered, per Step 1) output-column dictionary — the caller
    /// (<see cref="StarRocksRepository.PipelineAsync"/>) uses it as a Layer 2 (post-fetch) mask:
    /// every intermediate/final CTE's actual physical columns can be broader than this tracked
    /// set (the base CTE and any unqualified "select *" passthrough both select every raw table
    /// column, restricted or not), so the final result row must still be stripped down to exactly
    /// this set before it leaves <c>Iverson.StarRocks</c>.
    /// </summary>
    internal static (string Sql, DynamicParameters Param, Dictionary<string, string> LastCols) Build(
        StarRocksQuerySchema schema,
        PipelineRequest request,
        Func<string, StarRocksQuerySchema?> registry,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
    {
        var tracked = TrackAndValidate(schema, request, registry, authz);
        var byName  = tracked.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

        var param = new DynamicParameters();
        var sb = new StringBuilder();

        var baseWhere = StarRocksQueryBuilder.BuildWhere(
            p => StarRocksQueryBuilder.ResolveColumn(schema, p) is { } c ? $"`{c}`" : null,
            request.BaseWhere, request.BaseLogic, param, "s0_p", out _);

        // Primary-type row-ownership: same wrap-and-AND predicate BuildSearch/BuildAggregate/
        // BuildGroupBy append to their own WHERE — see StarRocksQueryBuilder for the rationale.
        if (authz is not null && authz.TryGetValue(schema.TypeName, out var primaryConstraint) && primaryConstraint.OwnerColumn is not null)
        {
            var ownerPredicate = $"`{primaryConstraint.OwnerColumn}` = @__ownerVal";
            param.Add("__ownerVal", primaryConstraint.OwnerValue);
            baseWhere = baseWhere.Length > 0 ? $"({baseWhere}) AND {ownerPredicate}" : ownerPredicate;
        }

        sb.Append($"WITH `{BaseStepName}` AS (SELECT * FROM `{schema.TableName}`");
        if (baseWhere.Length > 0) sb.Append($" WHERE {baseWhere}");
        sb.Append(')');

        var prev = BaseStepName;
        var emitted = new List<StepColumns> { byName[BaseStepName] };
        for (var i = 0; i < request.Steps.Count; i++)
        {
            var step  = request.Steps[i];
            var input = byName[string.IsNullOrEmpty(step.Reads) ? prev : step.Reads];
            sb.Append($", `{step.Name}` AS (");
            EmitStep(sb, step, input, emitted, registry, param, stepIdx: i + 1, authz);
            sb.Append(')');
            prev = step.Name;
            emitted.Add(byName[step.Name]);
        }

        sb.Append($" SELECT * FROM `{prev}`");
        var lastCols = byName[prev].Columns;
        if (request.OrderBy.Count > 0)
        {
            var orders = request.OrderBy
                .Select(s => $"`{lastCols[s.Property]}` {(s.Descending ? "DESC" : "ASC")}");
            sb.Append($" ORDER BY {string.Join(", ", orders)}");
        }
        var limit = request.Limit > 0 ? request.Limit : 10_000;
        sb.Append($" LIMIT {limit}");

        return (sb.ToString(), param, lastCols);
    }

    private static void EmitStep(
        StringBuilder sb,
        PipelineStep step,
        StepColumns input,
        List<StepColumns> emitted,
        Func<string, StarRocksQuerySchema?> registry,
        DynamicParameters param,
        int stepIdx,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
    {
        var isAggregate = step.GroupBy.Count > 0 || step.Metrics.Count > 0;

        if (isAggregate)
        {
            var where = StarRocksQueryBuilder.BuildWhere(
                p => input.Columns.TryGetValue(p, out var c) ? $"`{c}`" : null,
                step.Where, step.WhereLogic, param, $"s{stepIdx}_p", out _);

            var keyExprs  = new List<string>();
            var groupCols = new List<string>();
            foreach (var key in step.GroupBy)
            {
                var col = input.Columns[key.Field];
                if (key.DateTrunc == DateTrunc.None)
                {
                    keyExprs.Add($"`{col}`");
                    groupCols.Add($"`{col}`");
                }
                else
                {
                    var alias = OutputNameFor(key, input.Columns);
                    keyExprs.Add(
                        $"DATE_TRUNC('{key.DateTrunc.ToString().ToLowerInvariant()}', `{col}`) AS `{alias}`");
                    groupCols.Add($"`{alias}`");
                }
            }
            var metricExprs = step.Metrics.Select(m => EmitMetric(m, input.Columns));

            sb.Append($"SELECT {string.Join(", ", keyExprs.Concat(metricExprs))} FROM `{input.Name}`");
            if (where.Length > 0) sb.Append($" WHERE {where}");
            sb.Append($" GROUP BY {string.Join(", ", groupCols)}");

            var having = StarRocksQueryBuilder.BuildHaving(
                step.Having, SearchLogic.And, param, $"s{stepIdx}_h");
            if (having.Length > 0) sb.Append($" HAVING {having}");
            return;
        }

        // Non-aggregate step.
        var joinSources = ResolveJoinSources(step, emitted, registry, authz);
        var hasJoins = step.Joins.Count > 0;

        var nonAggWhere = StarRocksQueryBuilder.BuildWhere(
            p => input.Columns.TryGetValue(p, out var c)
                ? hasJoins ? $"`{input.Name}`.`{c}`" : $"`{c}`"
                : null,
            step.Where, step.WhereLogic, param, $"s{stepIdx}_p", out _);

        var selectParts = new List<string>();
        if (step.Select.Count > 0)
            foreach (var item in step.Select)
                selectParts.Add(EmitSelectItem(step, item, input, joinSources));
        else
            selectParts.Add("*");

        foreach (var w in step.Windows)
            selectParts.Add(EmitWindow(w, input.Columns));
        foreach (var d in step.Derive)
            selectParts.Add($"({d.Expr}) AS `{d.Alias}`");

        sb.Append($"SELECT {string.Join(", ", selectParts)} FROM `{input.Name}`");

        var joinIdx = 0;
        foreach (var join in step.Joins)
        {
            var src = joinSources[join.Source];
            var kind = join.Kind switch
            {
                JoinKind.Left  => "LEFT",
                JoinKind.Right => "RIGHT",
                JoinKind.Full  => "FULL",
                _              => "INNER"
            };
            // Entity sources join the physical table aliased as the type name;
            // step sources are CTEs joined by their own name (no alias needed).
            var joinedSchema = registry(src.Name);
            var isFreshType = joinedSchema is not null && emitted.All(s => !s.Name.Equals(src.Name, StringComparison.OrdinalIgnoreCase));
            var target = isFreshType
                ? $"`{joinedSchema!.TableName}` AS `{src.Name}`"
                : $"`{src.Name}`";
            var conds = join.On.Select(c =>
                $"`{input.Name}`.`{input.Columns[c.Left]}` = `{src.Name}`.`{src.Columns[c.Right]}`");

            // Joined-type ownership: appended to this JOIN's own ON clause (never the outer
            // WHERE) — same reasoning as BuildFromWithJoins in StarRocksQueryBuilder (a WHERE-
            // clause condition on the joined side would collapse LEFT/RIGHT/FULL to INNER
            // semantics). Only applies to a fresh registered type, never a prior step — a
            // step-to-step join needs no new predicate; that source was already filtered
            // upstream (same "already filtered upstream" reasoning already established for
            // Pipeline steps' own `where`).
            var ownerCond = "";
            if (isFreshType && authz is not null && authz.TryGetValue(src.Name, out var joinedConstraint) && joinedConstraint.OwnerColumn is not null)
            {
                var pName = $"s{stepIdx}_j{joinIdx}_owner";
                param.Add(pName, joinedConstraint.OwnerValue);
                ownerCond = $" AND `{src.Name}`.`{joinedConstraint.OwnerColumn}` = @{pName}";
            }

            sb.Append($" {kind} JOIN {target} ON {string.Join(" AND ", conds)}{ownerCond}");
            joinIdx++;
        }

        if (nonAggWhere.Length > 0) sb.Append($" WHERE {nonAggWhere}");
    }

    private static string EmitMetric(MetricSpec m, Dictionary<string, string> input)
    {
        var quotedName = $"`{m.Name.Replace("`", "``")}`";
        var isCountAll = m.Type == AggregationType.Count
            && string.IsNullOrEmpty(m.Field) && string.IsNullOrEmpty(m.Expression);
        if (isCountAll) return $"COUNT(*) AS {quotedName}";

        var fn = m.Type switch
        {
            AggregationType.Avg   => "AVG",
            AggregationType.Sum   => "SUM",
            AggregationType.Min   => "MIN",
            AggregationType.Max   => "MAX",
            AggregationType.Count => "COUNT",
            _ => throw new StarRocksQueryTranslationException(
                $"Metric '{m.Name}' has unsupported type '{m.Type}'.")
        };
        // m.Expression is raw trusted SQL — same posture as BuildMetricExpr in
        // StarRocksQueryBuilder; see the comment there.
        var arg = !string.IsNullOrEmpty(m.Expression) ? m.Expression : $"`{input[m.Field]}`";
        return $"{fn}({arg}) AS {quotedName}";
    }

    private static string EmitWindow(WindowFunction w, Dictionary<string, string> input)
    {
        var partition = string.IsNullOrEmpty(w.PartitionBy)
            ? ""
            : $"PARTITION BY `{input[w.PartitionBy]}` ";
        var order = $"ORDER BY `{input[w.OrderBy]}` {(w.Descending ? "DESC" : "ASC")}";
        var over  = $"OVER ({partition}{order})";
        var offset = w.Offset > 0 ? w.Offset : 1;

        var call = w.Kind switch
        {
            WindowFunctionKind.RowNumber  => "ROW_NUMBER()",
            WindowFunctionKind.Rank       => "RANK()",
            WindowFunctionKind.DenseRank  => "DENSE_RANK()",
            WindowFunctionKind.RunningSum => $"SUM(`{input[w.Field]}`)",
            WindowFunctionKind.RunningAvg => $"AVG(`{input[w.Field]}`)",
            WindowFunctionKind.Lag        => $"LAG(`{input[w.Field]}`, {offset})",
            WindowFunctionKind.Lead       => $"LEAD(`{input[w.Field]}`, {offset})",
            _ => throw new StarRocksQueryTranslationException(
                $"Window '{w.Alias}' has unsupported kind '{w.Kind}'.")
        };
        return $"{call} {over} AS `{w.Alias}`";
    }

    private static string EmitSelectItem(
        PipelineStep step, SelectItem item, StepColumns input,
        Dictionary<string, StepColumns> joinSources)
    {
        var hasJoins = step.Joins.Count > 0;

        StepColumns source;
        string sqlAlias;
        if (string.IsNullOrEmpty(item.Source) ||
            item.Source.Equals(input.Name, StringComparison.OrdinalIgnoreCase))
        {
            source = input;
            sqlAlias = input.Name;
        }
        else
        {
            source = joinSources[item.Source];
            sqlAlias = source.Name;
        }

        if (item.All)
            return hasJoins ? $"`{sqlAlias}`.*" : "*";

        var col = source.Columns[item.Column];
        var qualified = hasJoins ? $"`{sqlAlias}`.`{col}`" : $"`{col}`";
        return string.IsNullOrEmpty(item.Alias) ? qualified : $"{qualified} AS `{item.Alias}`";
    }
}
