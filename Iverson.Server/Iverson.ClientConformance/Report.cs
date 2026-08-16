using System.Reflection;
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
///
/// <see cref="Assertions"/> carries every assertion the scenario made for this cell — passing and
/// failing alike — so the report's requirement tally (see <see cref="Report.RenderJson"/>) can
/// compute which requirement IDs this cell actually exercised. Every factory below takes it as a
/// required argument, not an optional one: a scenario that skips before making any assertions
/// passes an empty list explicitly, rather than a cell silently existing with none. That is
/// deliberate — a shape where <c>ReportCell</c> could be built without its assertions is exactly
/// the shape under which a future scenario would forget to carry them, and its requirements would
/// then show up as untouched for no reason visible in that scenario's own code.
/// </summary>
public sealed record ReportCell(
    string Language,
    string Scenario,
    CellStatus Status,
    IReadOnlyList<Assertion> Assertions,
    string? Reason = null,
    string? Detail = null)
{
    public static ReportCell Ok(string language, string scenario, IReadOnlyList<Assertion> assertions) =>
        new(language, scenario, CellStatus.Ok, assertions);

    public static ReportCell Fail(string language, string scenario, string detail, IReadOnlyList<Assertion> assertions) =>
        new(language, scenario, CellStatus.Fail, assertions, Detail: detail);

    public static ReportCell Skip(string language, string scenario, string reason, IReadOnlyList<Assertion>? assertions = null) =>
        new(language, scenario, CellStatus.Skip, assertions ?? [], Reason: reason);

    public static ReportCell Xfail(string language, string scenario, string reason, IReadOnlyList<Assertion>? assertions = null) =>
        new(language, scenario, CellStatus.Xfail, assertions ?? [], Reason: reason);
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
        var languageWidth = languages.Count == 0 ? 8 : Math.Max(8, languages.Max(l => l.Length) + 2);
        sb.Append("scenario".PadRight(scenarioWidth)).Append("  ");
        foreach (var language in languages)
            sb.Append(language.PadRight(languageWidth));
        sb.AppendLine();

        foreach (var scenario in scenarios)
        {
            sb.Append(scenario.PadRight(scenarioWidth)).Append("  ");
            foreach (var language in languages)
            {
                var cell = _cells.FirstOrDefault(c => c.Language == language && c.Scenario == scenario);
                sb.Append((cell is null ? "-" : Symbol(cell.Status)).PadRight(languageWidth));
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

        sb.AppendLine($"requirements: {UntouchedRequirementIds().Count} untouched of {RegistryRequirementIds().Count} registered");

        return sb.ToString();
    }

    public string RenderJson() => JsonSerializer.Serialize(
        new
        {
            cells = _cells.Select(c => new
            {
                language = c.Language,
                scenario = c.Scenario,
                status = Symbol(c.Status),
                reason = c.Reason,
                detail = c.Detail,
                requirementsExercised = ExercisedRequirementIds(c),
            }),
            requirementsUntouched = UntouchedRequirementIds(),
        },
        new JsonSerializerOptions { WriteIndented = true });

    public void WriteJson(string path) => File.WriteAllText(path, RenderJson());

    /// <summary>
    /// The distinct, non-null requirement IDs cited by <paramref name="cell"/>'s assertions —
    /// passing and failing alike, since a requirement is "exercised" by having its assertion run
    /// at all, not by that assertion having passed. Internal (not private) so
    /// <see cref="ComputeUntouched"/>'s unit tests can exercise the same logic the JSON render
    /// path uses, without needing entries in the (currently empty) <see cref="Requirements"/>
    /// registry.
    /// </summary>
    internal static IReadOnlyList<string> ExercisedRequirementIds(ReportCell cell) =>
        cell.Assertions
            .Select(a => a.RequirementId)
            .Where(id => id is not null)
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Every requirement ID declared as a <c>public const string</c> on <see cref="Requirements"/>
    /// — reflected rather than hard-coded so this tally tracks the registry as it grows across
    /// later tasks without any change here.
    /// </summary>
    private static IReadOnlyList<string> RegistryRequirementIds() =>
        typeof(Requirements)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

    /// <summary>
    /// The registry IDs no cell in this report exercised at all. Printed as a count in
    /// <see cref="RenderText"/> and listed in full in <see cref="RenderJson"/> so a fully green
    /// matrix can never hide a requirement whose assertion simply never ran this time.
    /// </summary>
    public IReadOnlyList<string> UntouchedRequirementIds() =>
        ComputeUntouched(_cells, RegistryRequirementIds());

    /// <summary>
    /// The pure computation behind <see cref="UntouchedRequirementIds"/>: every
    /// <paramref name="registryIds"/> entry that no cell's assertions cited. Taken as an explicit
    /// parameter — rather than always reflecting off <see cref="Requirements"/> — so this can be
    /// unit-tested against a fake registry; the real registry is empty until later tasks add
    /// citations (T1 landed it with zero consts), so a test pinned to reflection alone could never
    /// observe a non-empty untouched set today.
    /// </summary>
    internal static IReadOnlyList<string> ComputeUntouched(
        IEnumerable<ReportCell> cells, IEnumerable<string> registryIds)
    {
        var exercised = cells
            .SelectMany(ExercisedRequirementIds)
            .ToHashSet(StringComparer.Ordinal);

        return registryIds
            .Where(id => !exercised.Contains(id))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static string Symbol(CellStatus status) => status switch
    {
        CellStatus.Ok => "ok",
        CellStatus.Fail => "FAIL",
        CellStatus.Skip => "skip",
        CellStatus.Xfail => "xfail",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}
