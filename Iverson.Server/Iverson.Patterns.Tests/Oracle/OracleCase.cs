using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Iverson.Patterns.Tests.Oracle;

/// <summary>One MATCH_RECOGNIZE case, in the shape of trino-483-cases.json. Row values are SQL literals.</summary>
public sealed record OracleCase(
    string Name,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    IReadOnlyList<string> PartitionBy,
    IReadOnlyList<(string Column, bool Descending)> OrderBy,
    IReadOnlyList<(string Name, string Expression)> Measures,
    RowsPerMatch RowsPerMatch,
    AfterMatchSkipKind Skip,
    string SkipVariable,
    string Pattern,
    IReadOnlyList<(string Name, IReadOnlyList<string> Variables)> Subsets,
    IReadOnlyList<(string Name, string Expression)> Define,
    IReadOnlyList<(string Source, string Alias)> Select)
{
    public override string ToString() => Name;

    public static IReadOnlyList<OracleCase> LoadPorted()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Oracle", "trino-483-cases.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.EnumerateArray().Select(c => new OracleCase(
            $"{c.GetProperty("method").GetString()}[{c.GetProperty("index").GetInt32()}]",
            Strings(c.GetProperty("columns")),
            c.GetProperty("rows").EnumerateArray().Select(Strings).ToList(),
            Strings(c.GetProperty("partitionBy")),
            c.GetProperty("orderBy").EnumerateArray().Select(o => (o[0].GetString()!, o[1].GetBoolean())).ToList(),
            Pairs(c.GetProperty("measures")),
            Enum.Parse<RowsPerMatch>(c.GetProperty("rowsPerMatch").GetString()!),
            Enum.Parse<AfterMatchSkipKind>(c.GetProperty("skip").GetString()!),
            c.GetProperty("skipVariable").GetString()!,
            c.GetProperty("pattern").GetString()!,
            c.GetProperty("subsets").EnumerateArray().Select(s => (s[0].GetString()!, Strings(s[1]))).ToList(),
            Pairs(c.GetProperty("define")),
            Pairs(c.GetProperty("select")))).ToList();

        static IReadOnlyList<string> Strings(JsonElement a) => a.EnumerateArray().Select(e => e.GetString()!).ToList();
        static IReadOnlyList<(string, string)> Pairs(JsonElement a) =>
            a.EnumerateArray().Select(p => (p[0].GetString()!, p[1].GetString()!)).ToList();
    }

    /// <summary>The Trino query for this case (the same rendering Appendix B's validation ran: 130/130 reproduced).</summary>
    public string ToSql()
    {
        var select = string.Join(", ", Select.Select(s => s.Source == "*" ? "*" : $"m.{s.Source} AS {s.Alias}"));
        var values = string.Join(", ", Rows.Select(r => "(" + string.Join(", ", r) + ")"));
        var body = new List<string>();
        if (PartitionBy.Count > 0) body.Add("PARTITION BY " + string.Join(", ", PartitionBy));
        if (OrderBy.Count > 0) body.Add("ORDER BY " + string.Join(", ", OrderBy.Select(o => o.Column + (o.Descending ? " DESC" : ""))));
        if (Measures.Count > 0) body.Add("MEASURES " + string.Join(", ", Measures.Select(m => $"{m.Expression} AS {m.Name}")));
        body.Add(RowsPerMatch switch
        {
            RowsPerMatch.OneRow => "ONE ROW PER MATCH",
            RowsPerMatch.AllRowsShowEmpty => "ALL ROWS PER MATCH SHOW EMPTY MATCHES",
            RowsPerMatch.AllRowsOmitEmpty => "ALL ROWS PER MATCH OMIT EMPTY MATCHES",
            _ => "ALL ROWS PER MATCH WITH UNMATCHED ROWS",
        });
        body.Add(Skip switch
        {
            AfterMatchSkipKind.PastLastRow => "AFTER MATCH SKIP PAST LAST ROW",
            AfterMatchSkipKind.ToNextRow => "AFTER MATCH SKIP TO NEXT ROW",
            AfterMatchSkipKind.ToFirst => "AFTER MATCH SKIP TO FIRST " + SkipVariable,
            _ => "AFTER MATCH SKIP TO LAST " + SkipVariable,
        });
        body.Add($"PATTERN ({Pattern})");
        if (Subsets.Count > 0) body.Add("SUBSET " + string.Join(", ", Subsets.Select(s => $"{s.Name} = ({string.Join(", ", s.Variables)})")));
        if (Define.Count > 0) body.Add("DEFINE " + string.Join(", ", Define.Select(d => $"{d.Name} AS {d.Expression}")));
        return $"SELECT {select} FROM (VALUES {values}) t({string.Join(", ", Columns)}) MATCH_RECOGNIZE ({string.Join(" ", body)}) AS m";
    }

    /// <summary>A SQL literal from the case file → the C# value a row source would hold.</summary>
    public static object? Literal(string sql) =>
        sql.Equals("null", StringComparison.OrdinalIgnoreCase) ? null
        : sql.Equals("true", StringComparison.OrdinalIgnoreCase) ? true
        : sql.Equals("false", StringComparison.OrdinalIgnoreCase) ? false
        : sql.StartsWith('\'') ? sql[1..^1].Replace("''", "'")
        : long.Parse(sql, CultureInfo.InvariantCulture);
}
