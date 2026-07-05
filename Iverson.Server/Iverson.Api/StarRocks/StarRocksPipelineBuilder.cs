using System.Text.RegularExpressions;
using Grpc.Core;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;

namespace Iverson.Api.StarRocks;

/// <summary>
/// One pipeline step's output column set: <see cref="Columns"/> maps a referenced
/// name (case-insensitive) to the canonical output column name emitted in SQL.
/// </summary>
internal sealed record StepColumns(string Name, Dictionary<string, string> Columns);

/// <summary>
/// Compiles a <see cref="PipelineRequest"/> into a single StarRocks CTE-chain query.
/// Pass 1 (<see cref="TrackAndValidate"/>) computes every step's output column set and
/// rejects invalid references as gRPC InvalidArgument before any SQL is built.
/// </summary>
internal static class StarRocksPipelineBuilder
{
    internal const string BaseStepName = "base";

    private static readonly Regex IdentifierRx = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex TokenRx      = new("[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

    // Identifiers a Derive expression may use besides input columns. Anything else —
    // including SELECT/FROM/WHERE, which blocks subqueries — fails validation.
    private static readonly HashSet<string> DeriveWhitelist = new(StringComparer.OrdinalIgnoreCase)
    {
        "SUM", "AVG", "MIN", "MAX", "COUNT", "OVER", "PARTITION", "BY", "ORDER",
        "ASC", "DESC", "COALESCE", "NULLIF", "ROUND", "ABS", "AND", "OR", "NOT", "NULL"
    };

    internal static IReadOnlyList<StepColumns> TrackAndValidate(
        SchemaDescriptor schema,
        PipelineRequest request,
        SchemaRegistry registry)
    {
        var steps = new List<StepColumns>();

        var baseColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [schema.KeyColumn.Name] = schema.KeyColumn.Name
        };
        foreach (var c in schema.ScalarColumns)
            baseColumns[c.Name] = c.Name;
        steps.Add(new StepColumns(BaseStepName, baseColumns));

        foreach (var clause in request.BaseWhere)
            RequireColumn(BaseStepName, baseColumns, clause.Property);

        foreach (var step in request.Steps)
        {
            ValidateStepName(step, steps, registry);
            var input = ResolveInput(step, steps);
            var output = ValidateStepAndComputeOutput(step, input, steps, registry);
            steps.Add(new StepColumns(step.Name, output));
        }

        // Final ORDER BY resolves against the last step's output.
        var last = steps[^1];
        foreach (var sort in request.OrderBy)
            RequireColumn(last.Name, last.Columns, sort.Property);

        return steps;
    }

    private static void ValidateStepName(
        PipelineStep step, List<StepColumns> earlier, SchemaRegistry registry)
    {
        if (string.IsNullOrEmpty(step.Name) || !IdentifierRx.IsMatch(step.Name))
            throw Invalid($"Step name '{step.Name}' is not a valid identifier.");
        if (step.Name.Equals(BaseStepName, StringComparison.OrdinalIgnoreCase))
            throw Invalid($"Step name '{step.Name}' is reserved for the implicit base step.");
        var duplicate = earlier.FirstOrDefault(
            s => s.Name.Equals(step.Name, StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null)
            throw Invalid($"Duplicate step name '{duplicate.Name}'.");
        if (registry.Get(step.Name) is not null)
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
        SchemaRegistry registry)
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
        if (isAggregate && step.GroupBy.Count == 0)
            throw Invalid($"Step '{step.Name}': metrics/HAVING require at least one GROUP BY key.");

        foreach (var clause in step.Where)
            RequireColumn(step.Name, input.Columns, clause.Property);

        // Join sources — resolution against prior steps or the schema registry.
        var joinSources = ResolveJoinSources(step, earlier, registry);

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
                if (string.IsNullOrEmpty(m.Name))
                    throw Invalid($"Step '{step.Name}': every metric requires an alias.");
                if (!string.IsNullOrEmpty(m.Field))
                    RequireColumn(step.Name, input.Columns, m.Field);
                else if (string.IsNullOrEmpty(m.Expression) && m.Type != AggregationType.Count)
                    throw Invalid($"Step '{step.Name}': metric '{m.Name}' requires a field or expression.");
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
                    foreach (var col in source.Columns.Values) AddOutput(col);
                }
                else
                {
                    RequireColumn(step.Name, source.Columns, item.Column);
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
        PipelineStep step, List<StepColumns> earlier, SchemaRegistry registry)
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

            var joinedSchema = registry.Get(join.Source)
                ?? throw Invalid($"Step '{step.Name}': join source '{join.Source}' is neither " +
                                 "an earlier step nor a registered type.");
            var cols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [joinedSchema.KeyColumn.Name] = joinedSchema.KeyColumn.Name
            };
            foreach (var c in joinedSchema.ScalarColumns) cols[c.Name] = c.Name;
            sources[joinedSchema.TypeName] = new StepColumns(joinedSchema.TypeName, cols);
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

    private static void ValidateDeriveExpr(
        string stepName, DeriveColumn d, Dictionary<string, string> available)
    {
        if (d.Expr.Contains(';') || d.Expr.Contains('\'') || d.Expr.Contains('`'))
            throw Invalid($"Step '{stepName}': derive '{d.Alias}' contains a forbidden character " +
                          "(no semicolons, quotes, or backticks).");
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

    private static RpcException Invalid(string message) =>
        new(new Status(StatusCode.InvalidArgument, message));
}
