using FluentAssertions;

namespace Iverson.Patterns.Tests.Oracle;

public static class OracleRunner
{
    public static async Task AssertAgreesAsync(TrinoClient trino, OracleCase c)
    {
        var sql = c.ToSql();
        var expected = await trino.QueryAsync(sql);

        List<List<object?[]>>? ours = null;
        Exception? ourError = null;
        try { ours = RunOurs(c, expected.Columns); }
        catch (Exception e) when (e is PatternValidationException or PatternEvaluationException) { ourError = e; }

        if (expected.Error is not null)
        {
            ourError.Should().NotBeNull($"Trino rejected {c} ({expected.Error}); so must we.\n{sql}");
            expected.ErrorType.Should().Be("USER_ERROR", $"{c}: only user errors are comparable.\n{sql}");
            return;
        }
        ourError.Should().BeNull($"Trino accepted {c}.\n{sql}");

        var trinoPartitions = GroupByPartition(c, expected.Columns, expected.Rows);
        var ourPartitions = ours!.Select(p => (Key: PartitionKey(c, expected.Columns, p.FirstOrDefault()), Rows: p))
            .Where(p => p.Rows.Count > 0).ToList();

        ourPartitions.Select(p => p.Key).Should().BeEquivalentTo(trinoPartitions.Keys, $"{c}: partitions differ.\n{sql}");
        foreach (var (key, rows) in ourPartitions)
            rows.Select(Render).Should().Equal(trinoPartitions[key].Select(Render), $"{c}: partition '{key}' differs.\n{sql}");
    }

    /// <summary>Our output, one list per partition, each row projected onto Trino's result columns.</summary>
    private static List<List<object?[]>> RunOurs(OracleCase c, IReadOnlyList<string> columns)
    {
        var request = new PatternRequest(PatternSource.TypeRows, c.PartitionBy, c.Pattern,
            c.Subsets.Select(s => new SubsetDefinition(s.Name, s.Variables)).ToList(),
            c.Define.Select(d => new NamedExpression(d.Name, d.Expression)).ToList(),
            c.Measures.Select(m => new NamedExpression(m.Name, m.Expression)).ToList(),
            c.RowsPerMatch, c.Skip, c.SkipVariable, TenantColumn: null);
        // Above the generator's largest program (6,083 instructions, seed 156); the 5,000 default is unit-tested in Task 3.
        var compiled = PatternQuery.Compile(request, maxProgramInstructions: 10_000);

        var rows = c.Rows.Select(r => (IDictionary<string, object?>)c.Columns.Zip(r)
                .ToDictionary(p => p.First, p => OracleCase.Literal(p.Second), StringComparer.OrdinalIgnoreCase))
            .ToList();
        var keys = c.PartitionBy.Select(p => (Column: p, Descending: false))
            .Concat(c.OrderBy).ToList();
        var ordered = rows.OrderBy(r => r, new RowOrder(keys)).ToList();   // OrderBy is stable

        var partitions = new List<List<IDictionary<string, object?>>>();
        foreach (var row in ordered)
        {
            if (partitions.Count == 0 || !SamePartition(c, partitions[^1][0], row)) partitions.Add([]);
            partitions[^1].Add(row);
        }

        var budget = new PatternBudget(maxActiveThreads: 10_000, maxSteps: 10_000_000);
        var aliasToSource = c.Select.Where(s => s.Source != "*")
            .ToDictionary(s => s.Alias, s => s.Source, StringComparer.OrdinalIgnoreCase);
        return partitions.Select(p => compiled.Run(p, (_, _) => null, budget)
                .Select(o => columns.Select(col => Lookup(o.Data, aliasToSource.GetValueOrDefault(col, col))).ToArray())
                .ToList())
            .ToList();
    }

    private static object? Lookup(IReadOnlyDictionary<string, object?> data, string name) =>
        data.First(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static bool SamePartition(OracleCase c, IDictionary<string, object?> a, IDictionary<string, object?> b) =>
        c.PartitionBy.All(p => Equals(a[p], b[p]));

    private static Dictionary<string, List<object?[]>> GroupByPartition(OracleCase c, IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows)
    {
        var result = new Dictionary<string, List<object?[]>>();
        foreach (var row in rows)
        {
            var key = PartitionKey(c, columns, row);
            if (!result.TryGetValue(key, out var list)) result[key] = list = [];
            list.Add(row);
        }
        return result;
    }

    /// <summary>Every partitioned ported case selects its partition columns (verified at plan time).</summary>
    private static string PartitionKey(OracleCase c, IReadOnlyList<string> columns, object?[]? row)
    {
        if (row is null || c.PartitionBy.Count == 0) return "";
        return string.Join("|", c.PartitionBy.Select(p =>
        {
            var alias = c.Select.FirstOrDefault(s => string.Equals(s.Source, p, StringComparison.OrdinalIgnoreCase)).Alias ?? p;
            var index = columns.ToList().FindIndex(col => string.Equals(col, alias, StringComparison.OrdinalIgnoreCase));
            return Render(row[index]);
        }));
    }

    private static string Render(object?[] row) => string.Join(", ", row.Select(Render));

    /// <summary>Type-insensitive numerics (our long 3 = Trino bigint 3; our double 3.5 = Trino double 3.5).</summary>
    private static string Render(object? v) => v switch
    {
        null => "NULL",
        long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
        double d when d == Math.Floor(d) && !double.IsInfinity(d) && Math.Abs(d) < 1e15 => ((long)d).ToString(System.Globalization.CultureInfo.InvariantCulture),
        double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        string s => "'" + s + "'",
        _ => v.ToString()!,
    };

    private sealed class RowOrder(IReadOnlyList<(string Column, bool Descending)> keys) : IComparer<IDictionary<string, object?>>
    {
        public int Compare(IDictionary<string, object?>? x, IDictionary<string, object?>? y)
        {
            foreach (var (column, descending) in keys)
            {
                var a = x![column];
                var b = y![column];
                int cmp = (a, b) switch
                {
                    (null, null) => 0,
                    (null, _) => 1,           // Trino's default: NULLS LAST
                    (_, null) => -1,
                    (string sa, string sb) => string.CompareOrdinal(sa, sb),   // Trino orders varchar by binary value
                    _ => Comparer<object>.Default.Compare(a, b),
                };
                if (cmp != 0) return descending && a is not null && b is not null ? -cmp : cmp;
            }
            return 0;
        }
    }
}
