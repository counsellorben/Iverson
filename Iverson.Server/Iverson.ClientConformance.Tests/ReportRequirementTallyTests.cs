using FluentAssertions;
using Iverson.ClientConformance;
using Xunit;

namespace Iverson.ClientConformance.Tests;

/// <summary>
/// The runtime requirement tally: which requirement IDs a report's cells actually exercised (an
/// assertion for that ID ran, pass or fail), and which registry IDs no cell touched at all. Tests
/// against a fake registry ID set rather than the real (currently empty) <see cref="Requirements"/>
/// class — see <see cref="Report.ComputeUntouched"/>'s doc comment for why.
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
}
