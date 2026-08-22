using System.Text.Json;
using FluentAssertions;
using Iverson.ClientConformance.Scenarios;
using Xunit;

namespace Iverson.ClientConformance.Tests;

/// <summary>
/// Unit coverage for S9's judgement, which is pure over reported data
/// (<see cref="ErrorContractScenario.Judge"/>, <see cref="ErrorContractScenario.SeededKey"/>,
/// <see cref="ErrorContractScenario.ReadBool"/>) and so is exercisable without a live stack.
///
/// Every test here names the mutation it would catch: an assertion that cannot be made to fail is
/// not evidence, and the point of this file is that each ERR requirement's cell goes red for
/// exactly the defect its statement describes and for nothing else. The two absence assertions are
/// covered independently on purpose — a client that raises on an absent row and a client that
/// fabricates a blank entity are different defects, and one conflated test would let either hide.
/// </summary>
public class ErrorContractScenarioTests
{
    private static readonly Guid Key = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private const string ServerDetail =
        "No schema registered for 'ErrorUnregisteredDoc'. Call RegisterSchema first.";

    private static PhaseDocument ReadDocument(params StepResult[] steps) => new("dotnet", "read", steps);

    private static StepResult PresentStep(bool found = true) =>
        new(ErrorContractScenario.PresentStepName, true,
            Entity: JsonSerializer.SerializeToElement(new { found }));

    private static StepResult MissingStep(bool? found = false, int? statusCode = null) =>
        new(ErrorContractScenario.MissingStepName, true,
            Entity: JsonSerializer.SerializeToElement(new { found, statusCode }));

    private static StepResult UnregisteredStep(
        int? statusCode = ErrorContractScenario.UnregisteredStatusCode, string? detail = ServerDetail) =>
        new(ErrorContractScenario.UnregisteredStepName, true,
            Entity: JsonSerializer.SerializeToElement(new { statusCode, detail }));

    private static IReadOnlyList<Assertion> JudgeHappy(
        StepResult? present = null,
        StepResult? missing = null,
        StepResult? unregistered = null,
        Guid? seeded = null) =>
        ErrorContractScenario.Judge("dotnet", seeded ?? Key, ReadDocument(
            present ?? PresentStep(), missing ?? MissingStep(), unregistered ?? UnregisteredStep()));

    private static IReadOnlyList<Assertion> Cited(IReadOnlyList<Assertion> assertions, string requirementId) =>
        assertions.Where(a => a.RequirementId == requirementId).ToList();

    private static Assertion Named(IReadOnlyList<Assertion> assertions, string fragment) =>
        assertions.Single(a => a.Name.Contains(fragment, StringComparison.Ordinal));

    // ── the happy path ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Judge_EveryStepAsTheContractRequires_AllAssertionsPass()
    {
        JudgeHappy().Should().OnlyContain(a => a.Passed);
    }

    [Fact]
    public void Judge_CitesEachErrRequirementItOwns()
    {
        var assertions = JudgeHappy();

        Cited(assertions, "IVC-ERR-004").Should().HaveCount(2);
        Cited(assertions, "IVC-ERR-005").Should().ContainSingle();
        Cited(assertions, "IVC-ERR-002").Should().ContainSingle();
    }

    /// <summary>
    /// The backstop carries no requirement ID, per the standard's ERR authoring notes: "a row that
    /// exists is found" is LIFE's claim, not an ERR statement. A mutation that cited an ERR const
    /// here would let the backstop discharge a requirement it is strictly weaker than.
    /// </summary>
    [Fact]
    public void Judge_TheBackstopAssertionCarriesNoRequirementId()
    {
        Named(JudgeHappy(), "finds the row this run seeded").RequirementId.Should().BeNull();
    }

    // ── the backstop ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Catches: a read path that finds nothing ever. Both absence assertions still pass here —
    /// which is precisely why the backstop exists and why it must be read as the cell's cause.
    /// </summary>
    [Fact]
    public void Judge_PositiveControlDidNotFindTheSeededRow_BackstopFailsWhileAbsenceAssertionsStillPass()
    {
        var assertions = JudgeHappy(present: PresentStep(found: false));

        Named(assertions, "finds the row this run seeded").Passed.Should().BeFalse();
        Cited(assertions, "IVC-ERR-004").Should().OnlyContain(a => a.Passed);
    }

    /// <summary>
    /// Catches: a write phase that seeded nothing, so the control read had no existing row to find
    /// and "reports absence" would be true of every key by construction.
    /// </summary>
    [Fact]
    public void Judge_NoSeededKey_BackstopFailsNamingTheConsequence()
    {
        // Called directly rather than through JudgeHappy: that helper defaults a null `seeded` to
        // Key, so it cannot express "the write phase reported no key" at all.
        var assertions = ErrorContractScenario.Judge("dotnet", null, ReadDocument(
            PresentStep(), MissingStep(), UnregisteredStep()));
        var backstop = Named(assertions, "finds the row this run seeded");

        backstop.Passed.Should().BeFalse();
        backstop.Detail.Should().Contain("finds nothing ever");
    }

    [Fact]
    public void Judge_PositiveControlStepMissing_BackstopFailsRatherThanBeingSkipped()
    {
        var assertions = ErrorContractScenario.Judge("dotnet", Key, ReadDocument(MissingStep(), UnregisteredStep()));

        Named(assertions, "finds the row this run seeded").Passed.Should().BeFalse();
    }

    // ── IVC-ERR-004: absence is reported, and reported as a completed call ────────────────────

    /// <summary>Catches: a client that fabricates a blank entity for a key no row exists under.</summary>
    [Fact]
    public void Judge_AbsentKeyReadReturnedAnEntity_Err004AbsenceAssertionFails()
    {
        var assertions = JudgeHappy(missing: MissingStep(found: true));

        Named(assertions, "reports absence rather than an entity").Passed.Should().BeFalse();
        Named(assertions, "reports absence rather than an entity").RequirementId.Should().Be("IVC-ERR-004");
    }

    /// <summary>
    /// Catches: a client that turns the server's Success=false envelope into a thrown status. The
    /// absence half is deliberately left failing too here — a raised status means the library
    /// returned no found/not-found flag at all — but the two are separate assertions so the report
    /// names which shape of the contract was broken.
    /// </summary>
    [Fact]
    public void Judge_AbsentKeyReadRaisedAStatus_Err004CompletionAssertionFails()
    {
        var assertions = JudgeHappy(missing: MissingStep(found: null, statusCode: 5));
        var completion = Named(assertions, "completed rather than failing with a status");

        completion.Passed.Should().BeFalse();
        completion.RequirementId.Should().Be("IVC-ERR-004");
        completion.Detail.Should().Contain("5");
    }

    /// <summary>
    /// The two ERR-004 assertions must be independently falsifiable: a client that reports absence
    /// but ALSO raises would otherwise be indistinguishable from one that only raises.
    /// </summary>
    [Fact]
    public void Judge_AbsentKeyReadReportedAbsenceButAlsoRaised_OnlyTheCompletionAssertionFails()
    {
        var assertions = JudgeHappy(missing: MissingStep(found: false, statusCode: 5));

        Named(assertions, "reports absence rather than an entity").Passed.Should().BeTrue();
        Named(assertions, "completed rather than failing with a status").Passed.Should().BeFalse();
    }

    [Fact]
    public void Judge_AbsentKeyStepMissing_BothErr004AssertionsFailRatherThanBeingSkipped()
    {
        var assertions = ErrorContractScenario.Judge("dotnet", Key, ReadDocument(PresentStep(), UnregisteredStep()));

        Cited(assertions, "IVC-ERR-004").Should().HaveCount(2).And.OnlyContain(a => !a.Passed);
    }

    [Fact]
    public void Judge_AbsentKeyStepItselfBroke_BothErr004AssertionsFail()
    {
        var broken = new StepResult(ErrorContractScenario.MissingStepName, false, Error: "channel closed");
        var assertions = JudgeHappy(missing: broken);

        Cited(assertions, "IVC-ERR-004").Should().OnlyContain(a => !a.Passed);
    }

    // ── IVC-ERR-005: the unregistered-type write ─────────────────────────────────────────────

    /// <summary>Catches: a server (or client) that answers an unregistered type with some other code.</summary>
    [Fact]
    public void Judge_UnregisteredWriteRefusedWithTheWrongCode_Err005Fails()
    {
        var assertions = JudgeHappy(unregistered: UnregisteredStep(statusCode: 7));

        Cited(assertions, "IVC-ERR-005").Should().ContainSingle().Which.Passed.Should().BeFalse();
    }

    /// <summary>
    /// Catches: an unregistered-type write the server ACCEPTED. The driver reports that as a null
    /// status code, and the assertion must read that as a failure rather than as "nothing observed".
    /// </summary>
    [Fact]
    public void Judge_UnregisteredWriteWasAccepted_Err005FailsNamingTheAcceptance()
    {
        var assertion = Cited(JudgeHappy(unregistered: UnregisteredStep(statusCode: null)), "IVC-ERR-005").Single();

        assertion.Passed.Should().BeFalse();
        assertion.Detail.Should().Contain("ACCEPTED");
    }

    [Fact]
    public void Judge_UnregisteredStepMissing_Err005FailsRatherThanBeingSkipped()
    {
        var assertions = ErrorContractScenario.Judge("dotnet", Key, ReadDocument(PresentStep(), MissingStep()));

        Cited(assertions, "IVC-ERR-005").Should().ContainSingle().Which.Passed.Should().BeFalse();
    }

    // ── IVC-ERR-002: the client library preserves the server's detail ─────────────────────────

    /// <summary>
    /// Catches: a client library that discards the server's status detail and substitutes wording
    /// of its own. This is the one message-preservation observation made through a client library
    /// rather than the orchestrator's own channel.
    /// </summary>
    [Fact]
    public void Judge_RefusalDetailDoesNotNameTheType_Err002Fails()
    {
        var assertions = JudgeHappy(unregistered: UnregisteredStep(detail: "the request could not be completed"));

        Cited(assertions, "IVC-ERR-002").Should().ContainSingle().Which.Passed.Should().BeFalse();
    }

    /// <summary>Catches: a client that surfaces the code but drops the detail entirely.</summary>
    [Fact]
    public void Judge_NoDetailReported_Err002FailsNamingTheDroppedMessage()
    {
        var assertion = Cited(JudgeHappy(unregistered: UnregisteredStep(detail: null)), "IVC-ERR-002").Single();

        assertion.Passed.Should().BeFalse();
        assertion.Detail.Should().Contain("did not hand the server's message");
    }

    /// <summary>
    /// ERR-002 and ERR-005 must be independently falsifiable: a right-code/wrong-message refusal
    /// and a wrong-code/right-message one are different regressions.
    /// </summary>
    [Fact]
    public void Judge_WrongCodeButCorrectDetail_OnlyErr005Fails()
    {
        var assertions = JudgeHappy(unregistered: UnregisteredStep(statusCode: 3));

        Cited(assertions, "IVC-ERR-005").Should().OnlyContain(a => !a.Passed);
        Cited(assertions, "IVC-ERR-002").Should().OnlyContain(a => a.Passed);
    }

    // ── SeededKey / ReadBool ──────────────────────────────────────────────────────────────────

    [Fact]
    public void SeededKey_ReadsTheLanguagesOwnErrorDocKey()
    {
        var keys = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["go"] = new Dictionary<string, string> { [ErrorContractScenario.RowKeyName] = Key.ToString() },
        };

        ErrorContractScenario.SeededKey(keys, "go").Should().Be(Key);
        ErrorContractScenario.SeededKey(keys, "java").Should().BeNull();
    }

    [Fact]
    public void SeededKey_UnparsableValue_IsNullRatherThanThrowing()
    {
        var keys = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["go"] = new Dictionary<string, string> { [ErrorContractScenario.RowKeyName] = "not-a-uuid" },
        };

        ErrorContractScenario.SeededKey(keys, "go").Should().BeNull();
    }

    /// <summary>
    /// A missing flag must stay null rather than collapsing to false: folding it in would make
    /// "the library reported nothing" indistinguishable from "the library reported absence", which
    /// is exactly the case IVC-ERR-004's first assertion exists to separate.
    /// </summary>
    [Fact]
    public void ReadBool_MissingOrNonBooleanProperty_IsNullNotFalse()
    {
        var entity = JsonSerializer.SerializeToElement(new { found = (bool?)null, other = "x" });

        ErrorContractScenario.ReadBool(entity, "found").Should().BeNull();
        ErrorContractScenario.ReadBool(entity, "absent").Should().BeNull();
        ErrorContractScenario.ReadBool(entity, "other").Should().BeNull();
        ErrorContractScenario.ReadBool(null, "found").Should().BeNull();
    }

    [Fact]
    public void ReadBool_TrueAndFalse_AreBothRead()
    {
        var entity = JsonSerializer.SerializeToElement(new { yes = true, no = false });

        ErrorContractScenario.ReadBool(entity, "yes").Should().BeTrue();
        ErrorContractScenario.ReadBool(entity, "no").Should().BeFalse();
    }

    // ── register-phase capture ────────────────────────────────────────────────────────────────

    [Fact]
    public void TryCaptureDescriptor_NoRegisterStep_IsAFailureNamingTheStep()
    {
        var (descriptor, failure) = ErrorContractScenario.TryCaptureDescriptor(
            new PhaseDocument("dotnet", "register", [new StepResult("something_else", true)]));

        descriptor.Should().BeNull();
        failure.Should().Contain(ErrorContractScenario.RegisterStepName);
    }

    [Fact]
    public void TryCaptureDescriptor_RegisterStepFailed_ReportsItsError()
    {
        var (descriptor, failure) = ErrorContractScenario.TryCaptureDescriptor(
            new PhaseDocument("dotnet", "register",
                [new StepResult(ErrorContractScenario.RegisterStepName, false, Error: "boom")]));

        descriptor.Should().BeNull();
        failure.Should().Be("boom");
    }

    // ── RunAsync plumbing and the read-phase grading seam ─────────────────────────────────────
    //
    // Everything above judges. NOTHING above proved the judgement ever REACHES a cell — this file
    // had zero RunAsync coverage, and deleting the Judge wiring in RunAsync left the whole suite
    // green while the scenario verified nothing. GradeReads is that wiring, extracted so it is
    // callable without a live stack; the tests below are what redden when it is dropped.
    // RunAsync's own phase plumbing is exercised the way SchemaCatalogScenarioTests exercises its
    // own: repoRoot "/tmp" has no driver project, so every driver breaks loudly and predictably.

    private static DriverContext Context() => new(
        Scenario: ErrorContractScenario.Name,
        Type: string.Empty,
        Tenant: "iverson-loadtest-dynamic",
        GrpcUrl: "http://localhost:5000",
        ClientId: "client-id",
        ClientSecret: "client-secret",
        TokenEndpoint: "http://localhost:9000/application/o/token/",
        ActingToken: "acting-token",
        OwnerId: "owner-id",
        IdPrefix: "s9-");

    private static ErrorContractScenario BuildScenario(string repoRoot = "/tmp")
    {
        var channel = Grpc.Net.Client.GrpcChannel.ForAddress("http://localhost:1");
        return new ErrorContractScenario(
            new DriverRunner(repoRoot: repoRoot),
            new Reregistrar(new Iverson.Client.Contracts.ObjectMappingService.ObjectMappingServiceClient(channel)));
    }

    private static Dictionary<string, ErrorContractScenario.LanguageState> States(params string[] languages) =>
        languages.ToDictionary(l => l, _ => new ErrorContractScenario.LanguageState(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// THE mutation this test exists for: deleting the <c>Judge</c> call inside
    /// <c>GradeReads</c>. That used to leave every suite green while no ERR assertion reached a
    /// cell at all — the coverage gate stayed green too, because its Check2 is a source substring
    /// match and <c>Judge</c> still textually held the citations.
    /// </summary>
    [Fact]
    public void GradeReads_TheReadPhaseJudgement_ReachesTheCellCarryingItsErrCitations()
    {
        var states = States("dotnet");

        var cells = BuildScenario().GradeReads(states,
            [("dotnet", ReadDocument(PresentStep(), MissingStep(found: true), UnregisteredStep()))]);

        var cell = cells.Should().ContainSingle().Subject;
        cell.Scenario.Should().Be(ErrorContractScenario.Name);
        cell.Assertions.Should().Contain(a => a.RequirementId == Requirements.ErrAbsentRowReadReportsAbsence);
        cell.Assertions.Should().Contain(a => a.RequirementId == Requirements.ErrUnregisteredTypeWriteIsFailedPrecondition);
    }

    [Fact]
    public void GradeReads_AFailingErrAssertion_TurnsThatLanguagesCellRedWithItsDetail()
    {
        var cells = BuildScenario().GradeReads(States("dotnet"),
            [("dotnet", ReadDocument(PresentStep(), MissingStep(found: true), UnregisteredStep()))]);

        var cell = cells.Should().ContainSingle().Subject;
        cell.Status.Should().Be(CellStatus.Fail);
        cell.Detail.Should().Contain("returned an entity for a key no row exists under");
    }

    [Fact]
    public void GradeReads_ALanguageWhoseDriverReportedNoReadDocument_IsNotGreen()
    {
        // The shape that used to render green having graded nothing.
        var cells = BuildScenario().GradeReads(States("dotnet", "go"),
            [("dotnet", ReadDocument(PresentStep(), MissingStep(), UnregisteredStep()))]);

        cells.Single(c => c.Language == "go").Status.Should().NotBe(CellStatus.Ok);
    }

    [Fact]
    public async Task RunAsync_NoLanguagesRequested_ReturnsNoCells() =>
        (await BuildScenario().RunAsync([], Context(), "acting-token")).Should().BeEmpty();

    [Fact]
    public async Task RunAsync_TheRegisterDriverBreaks_FailsEveryRequestedLanguage()
    {
        var cells = await BuildScenario().RunAsync(["dotnet", "python"], Context(), "acting-token");

        cells.Should().HaveCount(2);
        cells.Should().NotContain(c => c.Status == CellStatus.Ok);
        cells.Should().OnlyContain(c => c.Scenario == ErrorContractScenario.Name);
    }
}
