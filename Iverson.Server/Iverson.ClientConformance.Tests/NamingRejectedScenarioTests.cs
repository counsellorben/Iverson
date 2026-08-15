using FluentAssertions;
using Iverson.ClientConformance.Scenarios;
using Xunit;

namespace Iverson.ClientConformance.Tests;

public class NamingRejectedScenarioTests
{
    private static DriverContext Context() => new(
        Scenario: NamingRejectedScenario.Name,
        Type: string.Empty,
        Tenant: "iverson-loadtest-dynamic",
        GrpcUrl: "http://localhost:5000",
        ClientId: "client-id",
        ClientSecret: "client-secret",
        TokenEndpoint: "http://localhost:9000/application/o/token/",
        ActingToken: "acting-token",
        OwnerId: "owner-id",
        IdPrefix: "s2-");

    [Fact]
    public async Task RunAsync_DotnetAndJava_RenderAsSkip_WithoutInvokingAnyDriver()
    {
        // .NET and Java declare a relation's FK as a separate field from the nav property, so
        // there is no single wire name to misalign for them. This must never touch DriverRunner
        // at all — repoRoot "/tmp" would make any attempted build/exec fail loudly, which is
        // exactly how a regression here would be caught rather than silently building something.
        var scenario = new NamingRejectedScenario(new DriverRunner(repoRoot: "/tmp"));

        var cells = await scenario.RunAsync(["dotnet", "java"], Context());

        cells.Should().HaveCount(2);
        cells.Should().OnlyContain(c => c.Status == CellStatus.Skip);
        cells.Should().OnlyContain(c => c.Reason != null && c.Reason.Contains("separate field"));
    }

    [Fact]
    public async Task RunAsync_NoDriverCheckedLanguagesRequested_ReturnsOnlySkipCells()
    {
        var scenario = new NamingRejectedScenario(new DriverRunner(repoRoot: "/tmp"));

        var cells = await scenario.RunAsync(["java"], Context());

        cells.Should().ContainSingle();
        cells[0].Language.Should().Be("java");
        cells[0].Status.Should().Be(CellStatus.Skip);
    }

    [Fact]
    public void Judge_DriverReportedRegistrationOk_Fails_TheMisnamedRelationShouldHaveBeenRejected()
    {
        var document = new PhaseDocument("go", "register", [new StepResult("register", true)]);

        var cell = NamingRejectedScenario.Judge("go", document);

        cell.Status.Should().Be(CellStatus.Fail);
        cell.Detail.Should().Contain("failed client-side, before any RPC");
    }

    [Fact]
    public void Judge_ErrorNamesBothTheActualAndRequiredName_Passes()
    {
        var document = new PhaseDocument("python", "register",
        [
            new StepResult("register", false,
                Error: "PyBadArticle.writer_id declares a many_to_one relation to PyAuthor but " +
                       "is named 'WriterId' on the wire; a many_to_one foreign-key field must " +
                       "be named 'AuthorId' (rename the member to match)."),
        ]);

        var cell = NamingRejectedScenario.Judge("python", document);

        cell.Status.Should().Be(CellStatus.Ok);
    }

    [Fact]
    public void Judge_ErrorMissingTheRequiredForeignKeyName_Fails()
    {
        var document = new PhaseDocument("typescript", "register",
        [
            new StepResult("register", false, Error: "some unrelated registration error"),
        ]);

        var cell = NamingRejectedScenario.Judge("typescript", document);

        cell.Status.Should().Be(CellStatus.Fail);
        cell.Detail.Should().Contain("required foreign-key name");
    }

    // Guards the "actual, misnamed member" assertion independently of the "required foreign-key
    // name" assertion above: an error message can legitimately name AuthorId (e.g. because it is
    // quoting the schema it expected) while never naming the member the driver actually declared
    // ('writer'/'writerId'/'WriterId'). Without this test, disabling the actual-member-name check
    // entirely left the whole suite green — found during Task 11's mutation pass.
    [Fact]
    public void Judge_ErrorMissingTheActualMemberName_Fails()
    {
        var document = new PhaseDocument("go", "register",
        [
            new StepResult("register", false,
                Error: "a many_to_one foreign-key field must be named 'AuthorId'"),
        ]);

        var cell = NamingRejectedScenario.Judge("go", document);

        cell.Status.Should().Be(CellStatus.Fail);
        cell.Detail.Should().Contain("actual, misnamed member");
    }

    [Fact]
    public void Judge_NoRegisterStepReported_Fails()
    {
        var document = new PhaseDocument("go", "register", [new StepResult("register_author", true)]);

        var cell = NamingRejectedScenario.Judge("go", document);

        cell.Status.Should().Be(CellStatus.Fail);
        cell.Detail.Should().Contain("no 'register' step");
    }
}
