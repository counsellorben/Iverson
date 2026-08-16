namespace Iverson.ClientConformance.Tests;

/// <summary>
/// Parses `| ID | Status | Kind | Statement |` requirement tables out of the standard's markdown.
/// Lives in the test project (not the orchestrator) because it exists purely to feed the coverage
/// gate and its own unit tests — nothing under <c>Iverson.ClientConformance/</c> needs it.
///
/// A table starts at a line that is exactly the header <c>| ID | Status | Kind | Statement |</c>
/// and ends at the first line, inside the table, that does not start with <c>|</c> (a blank line
/// or a heading). Any other axis table (e.g. the nine-axis table) or the entry-format table
/// (<c>| Column | Meaning |</c>) never opens a requirement table, so their rows are never parsed.
/// IDs mentioned in prose outside a table are likewise never parsed.
///
/// Critically: a `|`-leading line *inside* an open table that cannot be parsed as a well-formed
/// row (bad status casing, malformed ID shape, wrong column count, ...) does NOT silently close
/// the table. It is recorded as malformed and the scan continues, so a single bad row can never
/// make every subsequent row in that axis vanish from the parse — which would let the coverage
/// gate go green while those requirements have no const and no citing assertion.
/// </summary>
internal static class RequirementTableParser
{
    private static readonly System.Text.RegularExpressions.Regex IdCellPattern =
        new(@"^\|\s*(IVC-[A-Za-z]+-\d+)\s*\|\s*(Active|Retired)\s*\|", System.Text.RegularExpressions.RegexOptions.Compiled);

    public sealed record Result(List<(string Id, string Status)> Rows, List<string> MalformedLines);

    public static Result Parse(string markdown)
    {
        var rows = new List<(string Id, string Status)>();
        var malformed = new List<string>();
        var inRequirementTable = false;

        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (line.StartsWith("| ID | Status | Kind | Statement |", StringComparison.Ordinal))
            {
                inRequirementTable = true;
                continue;
            }

            if (!inRequirementTable)
            {
                continue;
            }

            var trimmed = line.Trim();

            if (trimmed.Length == 0 || !trimmed.StartsWith('|'))
            {
                // Blank line or a non-table line (e.g. the next heading) closes the table.
                inRequirementTable = false;
                continue;
            }

            if (IsSeparatorRow(trimmed))
            {
                continue;
            }

            var match = IdCellPattern.Match(line);
            if (match.Success)
            {
                rows.Add((match.Groups[1].Value, match.Groups[2].Value));
            }
            else
            {
                // A pipe-leading line inside the table that we could not parse. Recorded, not
                // dropped silently, and — unlike the old behaviour — does NOT end the table, so
                // any well-formed rows after it are still parsed.
                malformed.Add(line);
            }
        }

        return new Result(rows, malformed);
    }

    private static bool IsSeparatorRow(string trimmedLine) =>
        trimmedLine.Replace("-", string.Empty).Replace("|", string.Empty).Replace(":", string.Empty).Trim().Length == 0;
}
