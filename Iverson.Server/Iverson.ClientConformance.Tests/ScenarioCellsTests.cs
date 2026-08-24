using FluentAssertions;
using Iverson.ClientConformance.Scenarios;
using Xunit;

namespace Iverson.ClientConformance.Tests;

/// <summary>
/// The falsifiability tests for the ONE cell-building rule every scenario now shares.
///
/// <para>These exist because of a specific, reproduced green-wash: <c>Cell</c> used to be
/// duplicated verbatim in seven scenario files, and mutating it to
/// <c>return ReportCell.Ok(language, Name, state.Assertions);</c> in five of those seven — the
/// interop, query, vector-search, identity and error-contract copies — left the whole suite green.
/// Five scenarios could report every language as passing while their assertions failed, and nothing
/// in the test suite noticed. Hoisting the rule into <see cref="ScenarioCells"/> makes that mutation
/// have exactly one place to live, and this file is what kills it there — once, for every scenario
/// that exists now and every one added later.</para>
/// </summary>
public class ScenarioCellsTests
{
    private const string Scenario = "a-scenario";

    private sealed class State : ILanguageState
    {
        public List<Assertion> Assertions { get; } = [];
        public ReportCell? Terminal { get; set; }
    }

    private static State StateWith(params Assertion[] assertions)
    {
        var state = new State();
        state.Assertions.AddRange(assertions);
        return state;
    }

    // ── Cell: the green-wash mutation ─────────────────────────────────────────────────────────

    /// <summary>
    /// THE mutation this file exists for: <c>Cell</c> returning Ok unconditionally.
    /// </summary>
    [Fact]
    public void Cell_AnyAssertionFailed_IsNotOk()
    {
        var state = StateWith(
            Assertion.Pass("something true", "ok"),
            Assertion.Fail("something false", "the observed detail"));

        var cell = ScenarioCells.Cell("go", Scenario, state);

        cell.Status.Should().Be(CellStatus.Fail);
        cell.Language.Should().Be("go");
        cell.Scenario.Should().Be(Scenario);
    }

    [Fact]
    public void Cell_FailureDetail_NamesEveryFailedAssertionAndItsDetail()
    {
        var state = StateWith(
            Assertion.Fail("first broken thing", "first detail"),
            Assertion.Pass("a passing one", "ignored"),
            Assertion.Fail("second broken thing", "second detail"));

        var cell = ScenarioCells.Cell("python", Scenario, state);

        cell.Detail.Should().Contain("first broken thing").And.Contain("first detail");
        cell.Detail.Should().Contain("second broken thing").And.Contain("second detail");
        cell.Detail.Should().NotContain("a passing one");
    }

    [Fact]
    public void Cell_EveryAssertionPassed_IsOkAndCarriesThemAll()
    {
        var state = StateWith(Assertion.Pass("one", "ok"), Assertion.Pass("two", "ok"));

        var cell = ScenarioCells.Cell("dotnet", Scenario, state);

        cell.Status.Should().Be(CellStatus.Ok);
        cell.Assertions.Should().HaveCount(2);
    }

    // ── Cell: a cell that graded nothing ──────────────────────────────────────────────────────

    /// <summary>
    /// The second green-wash shape, and the reason the empty arm exists: a scenario whose
    /// judgement never reached its cells — a dropped <c>Judge</c> call, a read phase that produced
    /// no documents — used to render a perfectly green row having verified nothing at all. Only
    /// the requirement tally showed it, and the tally affected nothing.
    /// </summary>
    [Fact]
    public void Cell_NoAssertionsAtAll_IsNotOk()
    {
        var cell = ScenarioCells.Cell("typescript", Scenario, new State());

        cell.Status.Should().Be(CellStatus.Fail);
        cell.Detail.Should().Contain("graded no assertions");
    }

    // ── Cell: the terminal outcome wins ───────────────────────────────────────────────────────

    [Fact]
    public void Cell_TerminalSkipIsSet_ReturnsThatSkipRatherThanGradingAssertions()
    {
        var state = StateWith(Assertion.Fail("would have failed", "detail"));
        state.Terminal = ReportCell.Skip("java", Scenario, "no toolchain", state.Assertions);

        ScenarioCells.Cell("java", Scenario, state).Status.Should().Be(CellStatus.Skip);
    }

    [Fact]
    public void Cell_TerminalFailIsSet_ReturnsThatFailRatherThanAFabricatedOk()
    {
        var state = new State
        {
            Terminal = ReportCell.Fail("go", Scenario, "driver broke during the read phase", []),
        };

        var cell = ScenarioCells.Cell("go", Scenario, state);

        cell.Status.Should().Be(CellStatus.Fail);
        cell.Detail.Should().Contain("driver broke");
    }

    // ── Alive ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Alive_ExcludesExactlyTheLanguagesWhoseRowAlreadyEnded()
    {
        var ended = new State { Terminal = ReportCell.Skip("java", Scenario, "no toolchain", []) };
        var states = new Dictionary<string, State>(StringComparer.OrdinalIgnoreCase)
        {
            ["dotnet"] = new(),
            ["java"] = ended,
            ["go"] = new(),
        };

        ScenarioCells.Alive(states).Should().BeEquivalentTo(["dotnet", "go"]);
    }

    [Fact]
    public void Alive_EveryRowEnded_IsEmptyRatherThanThrowing()
    {
        var states = new Dictionary<string, State>
        {
            ["dotnet"] = new() { Terminal = ReportCell.Fail("dotnet", Scenario, "broke", []) },
        };

        ScenarioCells.Alive(states).Should().BeEmpty();
    }

    // ── FailEveryLanguage ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void FailEveryLanguage_ProducesOneRedCellPerRequestedLanguage_CarryingTheSharedDetail()
    {
        var cells = ScenarioCells.FailEveryLanguage(["dotnet", "python", "go"], Scenario, "the precondition failed");

        cells.Should().HaveCount(3);
        cells.Should().OnlyContain(c => c.Status == CellStatus.Fail);
        cells.Should().OnlyContain(c => c.Scenario == Scenario);
        cells.Should().OnlyContain(c => c.Detail!.Contains("the precondition failed"));
        cells.Select(c => c.Language).Should().Equal("dotnet", "python", "go");
    }

    // ── Truncate ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Truncate_ShortText_IsKeptWholeAndTrimmed() =>
        ScenarioCells.Truncate("  boom  ").Should().Be("boom");

    [Fact]
    public void Truncate_LongText_KeepsTheTailBecauseThatIsWhereTheFailureIs()
    {
        var text = new string('a', 100) + new string('b', ScenarioCells.StderrTailLength);

        var truncated = ScenarioCells.Truncate(text);

        truncated.Should().HaveLength(ScenarioCells.StderrTailLength);
        truncated.Should().NotContain("a");
    }
}
