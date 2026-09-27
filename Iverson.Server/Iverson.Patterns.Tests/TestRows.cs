namespace Iverson.Patterns.Tests;

internal static class TestRows
{
    /// <summary>Partition rows keyed case-insensitively, as both row sources build them (spec §3.2).</summary>
    public static List<IDictionary<string, object?>> Rows(string[] columns, params object?[][] values) =>
        values.Select(v => (IDictionary<string, object?>)columns.Zip(v)
                .ToDictionary(p => p.First, p => p.Second, StringComparer.OrdinalIgnoreCase))
            .ToList();
}
