namespace Iverson.ClientConformance.Scenarios;

/// <summary>
/// The per-language mutable state every multi-phase scenario accumulates while its phases run.
/// Declared as an interface purely so <see cref="ScenarioCells"/> can own the cell-building and
/// liveness rules once, rather than once per scenario: those rules are what decide whether a
/// (language, scenario) cell is green, and eight byte-identical private copies of them were eight
/// places a green-washing mutation could hide with only some of them under test.
/// </summary>
internal interface ILanguageState
{
    /// <summary>Every assertion the scenario has graded for this language so far.</summary>
    List<Assertion> Assertions { get; }

    /// <summary>Set when the row ended early (skip or a broken driver); stops later phases.</summary>
    ReportCell? Terminal { get; set; }
}

/// <summary>
/// The cell-building rules shared by every scenario. Previously duplicated verbatim in each
/// scenario file — <c>Cell</c> in seven copies, <c>Truncate</c> in eight, <c>Alive</c> in seven and
/// <c>FailEveryLanguage</c> in four — which meant the single most safety-critical line in the
/// harness ("does this cell go green?") had seven independent implementations and unit coverage of
/// only some. One copy, one test.
/// </summary>
internal static class ScenarioCells
{
    /// <summary>How much of a broken driver's stderr the failure detail keeps (the tail).</summary>
    internal const int StderrTailLength = 2000;

    /// <summary>
    /// The cell for one language, honouring an already-set terminal outcome. See the
    /// <see cref="Cell(string, string, IReadOnlyList{Assertion})"/> overload for the grading rule.
    /// </summary>
    internal static ReportCell Cell(string language, string scenario, ILanguageState state) =>
        state.Terminal ?? Cell(language, scenario, state.Assertions);

    /// <summary>
    /// Green only when this language was actually graded and every assertion passed.
    ///
    /// <para>The empty-assertion arm is not defensive padding: a scenario whose judgement never
    /// reached its cells — a dropped <c>Judge</c> call, a read phase that returned no documents —
    /// otherwise renders a perfectly green row while having verified nothing at all, and the
    /// requirement tally is the only place that shows up. A cell that graded nothing is a failure,
    /// not a pass.</para>
    /// </summary>
    internal static ReportCell Cell(string language, string scenario, IReadOnlyList<Assertion> assertions)
    {
        var failures = assertions.Where(a => !a.Passed).ToList();
        if (failures.Count > 0)
        {
            return ReportCell.Fail(language, scenario, string.Join(
                Environment.NewLine + "    ",
                failures.Select(f => $"{f.Name} — {f.Detail}")), assertions);
        }

        return assertions.Count > 0
            ? ReportCell.Ok(language, scenario, assertions)
            : ReportCell.Fail(language, scenario,
                "the scenario graded no assertions at all for this language, so this cell verifies " +
                "nothing — a cell that graded nothing is not a pass", assertions);
    }

    /// <summary>The languages whose row has not already ended (skip, or a broken driver).</summary>
    internal static IEnumerable<string> Alive<TState>(Dictionary<string, TState> states)
        where TState : ILanguageState =>
        states.Where(kv => kv.Value.Terminal is null).Select(kv => kv.Key).ToList();

    /// <summary>
    /// One red cell per requested language, for a shared precondition that failed before any
    /// language could be graded individually.
    /// </summary>
    internal static IReadOnlyList<ReportCell> FailEveryLanguage(
        IReadOnlyCollection<string> languages, string scenario, string detail) =>
        languages.Select(l => ReportCell.Fail(l, scenario, detail, [])).ToList();

    /// <summary>The tail of a driver's stderr, trimmed — the end is where the failure is.</summary>
    internal static string Truncate(string text) =>
        text.Length <= StderrTailLength ? text.Trim() : text[^StderrTailLength..].Trim();
}
