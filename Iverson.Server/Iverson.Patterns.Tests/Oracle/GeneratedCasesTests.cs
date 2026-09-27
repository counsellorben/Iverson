using Xunit;

namespace Iverson.Patterns.Tests.Oracle;

[Trait("Category", "Integration")]
[Collection(TrinoCollection.Name)]
public sealed class GeneratedCasesTests(TrinoContainerFixture fixture)
{
    public static TheoryData<int> Seeds()
    {
        var data = new TheoryData<int>();
        for (int seed = 0; seed < 200; seed++) data.Add(seed);
        return data;
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public Task Agrees_with_Trino(int seed) => OracleRunner.AssertAgreesAsync(fixture.Client, Generate(seed));

    private static readonly string[] Variables = ["A", "B", "C"];

    internal static OracleCase Generate(int seed)
    {
        var random = new Random(seed);
        string Spell(string v) => random.Next(3) switch { 0 => v, 1 => v.ToLowerInvariant(), _ => v };  // mixed case

        var used = new List<string>();
        string Primary(int depth)
        {
            int roll = random.Next(depth >= 2 ? 6 : 10);
            string p;
            if (roll < 6)
            {
                var v = Variables[random.Next(Variables.Length)];
                if (!used.Contains(v)) used.Add(v);
                p = Spell(v);
            }
            else if (roll < 8) p = "(" + Alternation(depth + 1) + ")";
            else if (roll < 9) p = "PERMUTE(" + string.Join(", ", Enumerable.Range(0, random.Next(2, 4)).Select(_ => Alternation(depth + 1))) + ")";
            else p = "{- " + Alternation(depth + 1) + " -}";
            string quantifier = random.Next(5) switch
            {
                0 => "*", 1 => "+", 2 => "?",
                3 => "{" + random.Next(0, 2) + "," + random.Next(2, 4) + "}",
                _ => "",
            };
            return p + quantifier + (quantifier.Length > 0 && random.Next(3) == 0 ? "?" : "");
        }
        string Concatenation(int depth) => string.Join(" ", Enumerable.Range(0, random.Next(1, 4)).Select(_ => Primary(depth)));
        string Alternation(int depth) => string.Join(" | ", Enumerable.Range(0, depth >= 2 ? 1 : random.Next(1, 3)).Select(_ => Concatenation(depth)));

        var pattern = (random.Next(8) == 0 ? "^ " : "") + Alternation(0) + (random.Next(8) == 0 ? " $" : "");

        string DefineFor(string v)
        {
            var q = Spell(v);
            return random.Next(7) switch
            {
                0 => $"{q}.v > PREV({q}.v)",
                1 => $"{q}.v < PREV({q}.v)",
                2 => $"{q}.v = {random.Next(10)}",
                3 => $"{q}.v > {random.Next(10)}",
                4 => $"COUNT({q}.*) <= {random.Next(1, 4)}",
                5 => $"FIRST({q}.v) < {q}.v",
                _ => "TRUE",
            };
        }
        var define = used.Where(_ => random.Next(4) != 0).Select(v => (Spell(v), DefineFor(v))).ToList();
        if (define.Count == 0) define.Add((Spell(used[0]), DefineFor(used[0])));   // Trino requires a DEFINE entry

        var someVariable = Spell(used[random.Next(used.Count)]);
        var measures = new List<(string, string)>
        {
            ("m1", "MATCH_NUMBER()"), ("m2", "CLASSIFIER()"), ("m3", "RUNNING LAST(v)"),
            ("m4", "FINAL LAST(v)"), ("m5", "COUNT(*)"), ("m6", $"SUM({someVariable}.v)"),
        };

        var rowsPerMatch = (RowsPerMatch)random.Next(4);
        var skip = (AfterMatchSkipKind)random.Next(4);
        var skipVariable = skip is AfterMatchSkipKind.ToFirst or AfterMatchSkipKind.ToLast ? someVariable : "";

        bool partitioned = random.Next(2) == 0;
        var rows = new List<IReadOnlyList<string>>();
        int id = 1;
        foreach (var part in partitioned ? new[] { "'x'", "'y'" } : ["'x'"])
            for (int i = 0, n = random.Next(3, 13); i < n; i++)
                rows.Add([part, (id++).ToString(System.Globalization.CultureInfo.InvariantCulture), random.Next(10).ToString(System.Globalization.CultureInfo.InvariantCulture)]);

        var select = new List<(string, string)>();
        if (partitioned) select.Add(("p", "p"));
        if (rowsPerMatch != RowsPerMatch.OneRow) { select.Add(("id", "id")); select.Add(("v", "v")); }
        select.AddRange(measures.Select(m => (m.Item1, m.Item1)));

        return new OracleCase($"generated[{seed}]", ["p", "id", "v"], rows,
            partitioned ? ["p"] : [], [("id", false)], measures, rowsPerMatch, skip, skipVariable,
            pattern, [], define, select);
    }
}
