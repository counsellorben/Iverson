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

    /// <summary>
    /// The scenario ran, but not for this language: it is a single orchestrator-side check with no
    /// client library involved, so it runs once and the other columns have nothing of their own to
    /// report. Distinct from <see cref="Skip"/>, which means "not observed" — an n/a cell's work
    /// WAS observed, in the canonical column its reason names. Never counts against the run.
    /// </summary>
    NotApplicable,
}

/// <summary>
/// One result for a given (language, scenario) pair. <see cref="Reason"/> is required whenever
/// <see cref="Status"/> is <see cref="CellStatus.Skip"/> or <see cref="CellStatus.NotApplicable"/>
/// — for the latter it carries the name of the column holding the real result. Failure detail (the assertion, the three
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

    public static ReportCell NotApplicable(string language, string scenario, string reason, IReadOnlyList<Assertion>? assertions = null) =>
        new(language, scenario, CellStatus.NotApplicable, assertions ?? [], Reason: reason);
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
    /// True only when every cell that could have passed did. Skip, Xfail and NotApplicable are all
    /// excluded: none of them represents a client that was observed getting the answer wrong.
    /// </summary>
    public bool AllPassed => _cells
        .Where(c => c.Status is not (CellStatus.Skip or CellStatus.Xfail or CellStatus.NotApplicable))
        .All(c => c.Status == CellStatus.Ok);

    /// <summary>
    /// Whether the run as a whole succeeded — the orchestrator's exit code, as a pure function of
    /// the three things that decide it.
    ///
    /// <para><paramref name="allPassed"/> alone is not enough. The plan's closing check is that a
    /// complete run leaves NO requirement untouched, and until this existed that check held only
    /// for as long as a human read the tally line: <c>requirementsUntouched</c> was printed,
    /// serialised, and affected nothing. A requirement whose only assertion silently stopped
    /// running would leave a fully green matrix and a zero exit code.</para>
    ///
    /// <para><paramref name="fullMatrix"/> is what keeps that from being wrong in the other
    /// direction. A narrowed run (<c>--scenarios query</c>, <c>--languages dotnet</c>) leaves most
    /// of the registry untouched BY CONSTRUCTION — 38 of 42 for <c>--scenarios query</c> — and
    /// failing it would make every targeted debugging run exit non-zero for no defect. The
    /// untouched set is only evidence of a gap when the run could legitimately have covered
    /// everything, which is exactly the un-narrowed run.</para>
    ///
    /// <para>Gated on the un-narrowed run rather than behind an opt-in
    /// <c>--require-full-coverage</c> flag deliberately: an opt-in flag leaves the DEFAULT
    /// invocation — the one CI and every hand-run of the harness use — advisory, which is the
    /// defect itself. Requiring a flag to get the check means the check is off wherever nobody
    /// remembered it.</para>
    /// </summary>
    internal static bool RunSucceeded(
        bool allPassed, bool fullMatrix, IReadOnlyCollection<string> untouched) =>
        allPassed && (!fullMatrix || untouched.Count == 0);

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

        foreach (var cell in _cells.Where(c => c.Status is CellStatus.Skip or CellStatus.Xfail or CellStatus.NotApplicable))
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
    /// path uses, against a fabricated registry rather than the real <see cref="Requirements"/>
    /// one.
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
    /// unit-tested against a fake registry. The real registry now holds 43 <c>Active</c> IDs and a
    /// full-matrix run leaves none of them untouched, so a test pinned to reflection alone could
    /// never observe a NON-empty untouched set, which is the case this computation exists for.
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
        CellStatus.NotApplicable => "n/a",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}
