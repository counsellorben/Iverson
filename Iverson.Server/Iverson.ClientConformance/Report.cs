using System.Text;
using System.Text.Json;

namespace Iverson.ClientConformance;

/// <summary>
/// A single cell in the languages-by-scenarios matrix.
/// </summary>
public enum CellStatus
{
    Ok,
    Fail,
    Skip,
    Xfail,
}

/// <summary>
/// One result for a given (language, scenario) pair. <see cref="Reason"/> is required whenever
/// <see cref="Status"/> is <see cref="CellStatus.Skip"/>. Failure detail (the assertion, the three
/// observed values, and the driver's captured stderr) is carried in <see cref="Detail"/> and is
/// expected whenever <see cref="Status"/> is <see cref="CellStatus.Fail"/>.
/// </summary>
public sealed record ReportCell(
    string Language,
    string Scenario,
    CellStatus Status,
    string? Reason = null,
    string? Detail = null)
{
    public static ReportCell Ok(string language, string scenario) =>
        new(language, scenario, CellStatus.Ok);

    public static ReportCell Fail(string language, string scenario, string detail) =>
        new(language, scenario, CellStatus.Fail, Detail: detail);

    public static ReportCell Skip(string language, string scenario, string reason) =>
        new(language, scenario, CellStatus.Skip, Reason: reason);

    public static ReportCell Xfail(string language, string scenario, string reason) =>
        new(language, scenario, CellStatus.Xfail, Reason: reason);
}

/// <summary>
/// The full languages-down, scenarios-across conformance matrix. Owns every assertion outcome for
/// a harness run; nothing outside the orchestrator renders pass/fail.
/// </summary>
public sealed class Report
{
    private readonly List<ReportCell> _cells = [];

    public IReadOnlyList<ReportCell> Cells => _cells;

    public void Add(ReportCell cell) => _cells.Add(cell);

    /// <summary>
    /// True only when every non-skipped, non-expected-fail cell passed.
    /// </summary>
    public bool AllPassed => _cells
        .Where(c => c.Status is not (CellStatus.Skip or CellStatus.Xfail))
        .All(c => c.Status == CellStatus.Ok);

    public string RenderText()
    {
        var sb = new StringBuilder();

        if (_cells.Count == 0)
        {
            sb.AppendLine("(no scenarios run)");
            return sb.ToString();
        }

        var languages = _cells.Select(c => c.Language).Distinct().ToList();
        var scenarios = _cells.Select(c => c.Scenario).Distinct().ToList();

        var scenarioWidth = Math.Max(8, scenarios.Count == 0 ? 0 : scenarios.Max(s => s.Length));
        sb.Append("scenario".PadRight(scenarioWidth)).Append("  ");
        foreach (var language in languages)
            sb.Append(language.PadRight(8));
        sb.AppendLine();

        foreach (var scenario in scenarios)
        {
            sb.Append(scenario.PadRight(scenarioWidth)).Append("  ");
            foreach (var language in languages)
            {
                var cell = _cells.FirstOrDefault(c => c.Language == language && c.Scenario == scenario);
                sb.Append((cell is null ? "-" : Symbol(cell.Status)).PadRight(8));
            }
            sb.AppendLine();
        }

        foreach (var cell in _cells.Where(c => c.Status is CellStatus.Skip or CellStatus.Xfail))
            sb.AppendLine($"  {cell.Language}/{cell.Scenario} {Symbol(cell.Status)}: {cell.Reason}");

        foreach (var cell in _cells.Where(c => c.Status == CellStatus.Fail))
        {
            sb.AppendLine($"  {cell.Language}/{cell.Scenario} FAIL:");
            sb.AppendLine($"    {cell.Detail}");
        }

        return sb.ToString();
    }

    public string RenderJson() => JsonSerializer.Serialize(
        _cells.Select(c => new
        {
            language = c.Language,
            scenario = c.Scenario,
            status = Symbol(c.Status),
            reason = c.Reason,
            detail = c.Detail,
        }),
        new JsonSerializerOptions { WriteIndented = true });

    public void WriteJson(string path) => File.WriteAllText(path, RenderJson());

    private static string Symbol(CellStatus status) => status switch
    {
        CellStatus.Ok => "ok",
        CellStatus.Fail => "FAIL",
        CellStatus.Skip => "skip",
        CellStatus.Xfail => "xfail",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}
