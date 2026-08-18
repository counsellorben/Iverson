using System.Text.RegularExpressions;

namespace Iverson.ClientConformance.Tests;

/// <summary>
/// Parses `#### Coverage` ledger tables (`| Area | Status | Evidence |`) out of the standard's
/// markdown, attributing each table to the `### AXIS — Name` heading currently in effect as the
/// document is walked top to bottom. Lives in the test project, alongside
/// <see cref="RequirementTableParser"/>, because it exists purely to feed the coverage-gate's
/// axis-completeness check and its own unit tests.
///
/// A table starts at a line that is exactly the header <c>| Area | Status | Evidence |</c> and
/// ends at the first line, inside the table, that does not start with <c>|</c> (a blank line or a
/// heading). Mirroring the hardened requirement parser, a `|`-leading line inside an open table
/// that cannot be parsed as a well-formed three-column row (wrong cell count) does NOT silently
/// close the table — it is recorded as malformed and the scan continues, so one bad row can never
/// make the rest of that axis's ledger vanish from the parse.
///
/// A coverage table's axis is whichever `### AXIS — Name` heading most recently preceded it, where
/// `AXIS` is a token drawn from the caller-supplied <paramref name="knownAxes"/> set (Requirement
/// A15). A coverage table appearing under a heading whose leading token is not a known axis — or
/// before any axis heading at all — is attributed to no axis (<c>Axis</c> is <c>null</c>).
/// </summary>
internal static class CoverageTableParser
{
    private const string Header = "| Area | Status | Evidence |";

    private static readonly Regex AxisHeadingPattern =
        new(@"^###\s+(\S+)\s+—", RegexOptions.Compiled);

    public sealed record CoverageRow(string? Axis, string Area, string Status, string Evidence);

    public sealed record Result(List<CoverageRow> Rows, List<string> MalformedLines);

    public static Result Parse(string markdown, IReadOnlyCollection<string> knownAxes)
    {
        var rows = new List<CoverageRow>();
        var malformed = new List<string>();
        var inCoverageTable = false;
        string? currentAxis = null;
        string? tableAxis = null;

        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            var headingMatch = AxisHeadingPattern.Match(line);
            if (headingMatch.Success)
            {
                var token = headingMatch.Groups[1].Value;
                currentAxis = knownAxes.Contains(token) ? token : null;
            }

            if (line.StartsWith(Header, StringComparison.Ordinal))
            {
                inCoverageTable = true;
                tableAxis = currentAxis;
                continue;
            }

            if (!inCoverageTable)
            {
                continue;
            }

            var trimmed = line.Trim();

            if (trimmed.Length == 0 || !trimmed.StartsWith('|'))
            {
                // Blank line or a non-table line (e.g. the next heading) closes the table.
                inCoverageTable = false;
                continue;
            }

            if (IsSeparatorRow(trimmed))
            {
                continue;
            }

            if (TryParseRow(trimmed, out var area, out var status, out var evidence))
            {
                rows.Add(new CoverageRow(tableAxis, area, status, evidence));
            }
            else
            {
                // A pipe-leading line inside the table that we could not parse as a well-formed
                // three-column row. Recorded, not dropped silently, and does NOT end the table, so
                // any well-formed rows after it are still parsed.
                malformed.Add(line);
            }
        }

        return new Result(rows, malformed);
    }

    private static bool TryParseRow(string trimmedLine, out string area, out string status, out string evidence)
    {
        area = string.Empty;
        status = string.Empty;
        evidence = string.Empty;

        if (!trimmedLine.StartsWith('|') || !trimmedLine.EndsWith('|'))
        {
            return false;
        }

        var inner = trimmedLine[1..^1];
        var parts = inner.Split('|');

        if (parts.Length != 3)
        {
            return false;
        }

        area = parts[0].Trim();
        status = parts[1].Trim();
        evidence = parts[2].Trim();

        return area.Length > 0 && status.Length > 0 && evidence.Length > 0;
    }

    private static bool IsSeparatorRow(string trimmedLine) =>
        trimmedLine.Replace("-", string.Empty).Replace("|", string.Empty).Replace(":", string.Empty).Trim().Length == 0;
}
