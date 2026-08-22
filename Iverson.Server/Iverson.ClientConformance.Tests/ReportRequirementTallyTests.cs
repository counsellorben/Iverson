using FluentAssertions;
using Iverson.ClientConformance;
using Xunit;

namespace Iverson.ClientConformance.Tests;

/// <summary>
/// The runtime requirement tally: which requirement IDs a report's cells actually exercised (an
/// assertion for that ID ran, pass or fail), and which registry IDs no cell touched at all. Tests
/// against a fabricated registry ID set rather than the real <see cref="Requirements"/> class —
/// see <see cref="Report.ComputeUntouched"/>'s doc comment for why.
/// </summary>
public class ReportRequirementTallyTests
{
    [Fact]
    public void ExercisedRequirementIds_CellWhoseAssertionsCiteNothing_IsEmpty()
    {
        var cell = ReportCell.Ok("dotnet", "s1",
        [
            Assertion.Pass("some check", "detail"),
            Assertion.Fail("another check", "detail"),
        ]);

        Report.ExercisedRequirementIds(cell).Should().BeEmpty();
    }

    [Fact]
    public void ExercisedRequirementIds_DistinctAndSorted_AcrossPassingAndFailingAssertions()
    {
        var cell = ReportCell.Fail("dotnet", "s1", "boom",
        [
            Assertion.Pass("check A", "ok", "IVC-REG-002"),
            Assertion.Fail("check B", "boom", "IVC-REG-001"),
            Assertion.Pass("check C", "ok", "IVC-REG-001"), // duplicate on purpose
        ]);

        Report.ExercisedRequirementIds(cell).Should().Equal("IVC-REG-001", "IVC-REG-002");
    }

    [Fact]
    public void ComputeUntouched_RequirementNeverCited_AppearsInUntouchedSet()
    {
        var cellWithNoCitations = ReportCell.Ok("dotnet", "s1",
        [
            Assertion.Pass("uncited check", "detail"),
        ]);

        var registryIds = new[] { "IVC-REG-001", "IVC-REG-002" };

        var untouched = Report.ComputeUntouched([cellWithNoCitations], registryIds);

        untouched.Should().BeEquivalentTo(registryIds);
    }

    [Fact]
    public void ComputeUntouched_RequirementCitedBySomeCell_IsExcluded()
    {
        var citing = ReportCell.Ok("dotnet", "s1",
        [
            Assertion.Pass("cited check", "detail", "IVC-REG-001"),
        ]);
        var notCiting = ReportCell.Ok("go", "s1",
        [
            Assertion.Pass("uncited check", "detail"),
        ]);

        var registryIds = new[] { "IVC-REG-001", "IVC-REG-002" };

        var untouched = Report.ComputeUntouched([citing, notCiting], registryIds);

        untouched.Should().Equal("IVC-REG-002");
    }

    [Fact]
    public void ComputeUntouched_EmptyRegistry_IsAlwaysEmpty()
    {
        var cell = ReportCell.Ok("dotnet", "s1", []);

        Report.ComputeUntouched([cell], []).Should().BeEmpty();
    }

    [Fact]
    public void RenderJson_IncludesPerCellExercisedIds_AndTopLevelUntouchedIds()
    {
        var report = new Report();
        report.Add(ReportCell.Ok("dotnet", "s1",
        [
            Assertion.Pass("cited check", "detail", "IVC-REG-001"),
        ]));

        var json = report.RenderJson();

        json.Should().Contain("requirementsExercised");
        json.Should().Contain("IVC-REG-001");
        json.Should().Contain("requirementsUntouched");
    }

    // ── the tally is a GATE, not a printout ───────────────────────────────────────────────────
    //
    // Until Report.RunSucceeded existed, `requirementsUntouched` was rendered, serialised, and
    // affected nothing: the exit code read cell statuses alone. The plan's closing check ("the
    // untouched set must be empty") therefore held only for as long as a human read the number.
    // These four tests are the gate; each names the mutation it kills.

    /// <summary>
    /// The mutation: making the coverage arm unconditional (`allPassed` alone, or dropping the
    /// `untouched.Count == 0` term). A full-matrix run that left a requirement unexercised must
    /// not exit 0, however green the grid looks.
    /// </summary>
    [Fact]
    public void RunSucceeded_FullMatrixRunWithAnUntouchedRequirement_Fails() =>
        Report.RunSucceeded(allPassed: true, fullMatrix: true, untouched: ["IVC-REG-002"])
            .Should().BeFalse();

    /// <summary>
    /// The mutation in the other direction: gating unconditionally rather than on the full matrix.
    /// `--scenarios query` correctly leaves 38 of 42 untouched, and failing that would make every
    /// targeted run exit non-zero for no defect at all.
    /// </summary>
    [Fact]
    public void RunSucceeded_NarrowedRunWithUntouchedRequirements_StillSucceeds() =>
        Report.RunSucceeded(allPassed: true, fullMatrix: false, untouched: ["IVC-REG-002", "IVC-QRY-001"])
            .Should().BeTrue();

    [Fact]
    public void RunSucceeded_FullMatrixRunWithNothingUntouched_Succeeds() =>
        Report.RunSucceeded(allPassed: true, fullMatrix: true, untouched: []).Should().BeTrue();

    [Fact]
    public void RunSucceeded_AFailedCell_FailsRegardlessOfCoverage()
    {
        Report.RunSucceeded(allPassed: false, fullMatrix: true, untouched: []).Should().BeFalse();
        Report.RunSucceeded(allPassed: false, fullMatrix: false, untouched: []).Should().BeFalse();
    }

    // ── what makes a run "full" ───────────────────────────────────────────────────────────────

    [Fact]
    public void CliFlags_NeitherAxisNarrowed_IsAFullMatrixRun() =>
        CliFlags.Parse([]).IsFullMatrix.Should().BeTrue();

    [Fact]
    public void CliFlags_ScenariosNarrowed_IsNotAFullMatrixRun() =>
        CliFlags.Parse(["--scenarios", "query"]).IsFullMatrix.Should().BeFalse();

    [Fact]
    public void CliFlags_LanguagesNarrowed_IsNotAFullMatrixRun() =>
        CliFlags.Parse(["--languages", "dotnet"]).IsFullMatrix.Should().BeFalse();

    [Fact]
    public void CliFlags_UnrelatedFlags_LeaveTheRunFull() =>
        CliFlags.Parse(["--json", "/tmp/x.json", "--keep"]).IsFullMatrix.Should().BeTrue();
}
