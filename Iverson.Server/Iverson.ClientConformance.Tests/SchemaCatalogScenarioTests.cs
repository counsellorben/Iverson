using System.Text.Json;
using FluentAssertions;
using Grpc.Net.Client;
using Iverson.Client.Contracts;
using Iverson.ClientConformance.Scenarios;
using Xunit;

namespace Iverson.ClientConformance.Tests;

/// <summary>
/// Unit coverage for S5's judgement, which is pure over reported data
/// (<see cref="SchemaCatalogScenario.JudgeCatalogue"/> / <see cref="SchemaCatalogScenario.ReadTypes"/>)
/// and so is exercisable without a live stack. <c>RunAsync</c>'s phase plumbing is exercised the
/// same way <c>InteropScenarioTests</c> exercises its own: repoRoot "/tmp" has no driver project,
/// so every driver breaks loudly and predictably.
/// </summary>
public class SchemaCatalogScenarioTests
{
    private static DriverContext Context() => new(
        Scenario: SchemaCatalogScenario.Name,
        Type: string.Empty,
        Tenant: "iverson-loadtest-dynamic",
        GrpcUrl: "http://localhost:5000",
        ClientId: "client-id",
        ClientSecret: "client-secret",
        TokenEndpoint: "http://localhost:9000/application/o/token/",
        ActingToken: "acting-token",
        OwnerId: "owner-id",
        IdPrefix: "s5-");

    private static SchemaCatalogScenario BuildScenario(string repoRoot = "/tmp")
    {
        var channel = GrpcChannel.ForAddress("http://localhost:1");
        var mapping = new ObjectMappingService.ObjectMappingServiceClient(channel);
        return new SchemaCatalogScenario(new DriverRunner(repoRoot: repoRoot), new Reregistrar(mapping));
    }

    private static TypeDescriptor Descriptor(string typeName, params string[] propertyNames)
    {
        var descriptor = new TypeDescriptor { TypeName = typeName };
        foreach (var name in propertyNames)
        {
            descriptor.Properties.Add(new PropertyDescriptor
            {
                Name = name,
                ClrType = name == "Id" ? ClrType.ClrGuid : ClrType.ClrString,
                IsKey = name == "Id",
            });
        }

        return descriptor;
    }

    private static JsonElement Catalogue(params (string Name, string[] Fields)[] types)
    {
        var payload = new
        {
            types = types.Select(t => new
            {
                name = t.Name,
                fields = t.Fields.Select(f => new { name = f }).ToList(),
                relations = Array.Empty<object>(),
            }).ToList(),
        };
        return JsonSerializer.SerializeToElement(payload);
    }

    private static PhaseDocument ReadDocument(params StepResult[] steps) =>
        new("dotnet", "read", steps);

    /// <summary>
    /// A descriptor as a driver reports it: protobuf JSON, which is what
    /// <c>Verifier.ParseDescriptor</c> reads back. Serializing with System.Text.Json instead would
    /// produce a shape the parser rejects, and the test would fail for the wrong reason.
    /// </summary>
    private static JsonElement DescriptorJson(TypeDescriptor descriptor) =>
        JsonDocument.Parse(Google.Protobuf.JsonFormatter.Default.Format(descriptor)).RootElement.Clone();

    private static Assertion Named(IReadOnlyList<Assertion> assertions, string fragment) =>
        assertions.Single(a => a.Name.Contains(fragment, StringComparison.Ordinal));


    // ── RunAsync driven end to end (closes Ruling 38's N3 residual) ────────────────────────────
    //
    // Everything above grades JudgeCatalogue / JudgeReadPhase directly. That is what left mutant N3
    // — deleting `JudgeReadPhase(...)` from RunAsync's read loop — surviving the whole suite: the
    // helpers stayed graded, the line that CALLS them did not, and Check2 reads source text rather
    // than the call graph, so all three IVC-SCH ids stayed "cited" while the axis graded nothing.
    // The IDriverRunner seam is what makes the test below possible; it asserts on the REPORT CELLS,
    // which is the only place the call site is observable.

    private static SchemaCatalogScenario DrivenScenario(
        ScriptedDriverRunner runner, RecordingReregistrar reregistrar) =>
        new(runner, reregistrar);

    [Fact]
    public async Task RunAsync_RegisterThenReadBothSucceed_TheCatalogueJudgementReachesTheCell()
    {
        var descriptor = Descriptor("DotNetAuthor", "Id", "TenantId", "OwnerId", "Name");
        var runner = new ScriptedDriverRunner()
            .Script(Phase.Register, new DriverPhaseOutcome.Success("dotnet",
                new PhaseDocument("dotnet", "register",
                [
                    new StepResult(
                        SchemaCatalogScenario.RegisterStepName, true,
                        TypeDescriptor: DescriptorJson(descriptor))
                ])))
            .Script(Phase.Read, new DriverPhaseOutcome.Success("dotnet",
                new PhaseDocument("dotnet", "read",
                [
                    new StepResult(
                        SchemaCatalogScenario.ReadStepName, true,
                        Entity: Catalogue(("DotNetAuthor", ["Id", "TenantId", "OwnerId", "Name"])))
                ])));

        var reregistrar = new RecordingReregistrar();
        var cells = await DrivenScenario(runner, reregistrar).RunAsync(["dotnet"], Context(), "acting-token");

        var cell = cells.Should().ContainSingle().Subject;
        cell.Status.Should().Be(CellStatus.Ok);
        reregistrar.Calls.Should().ContainSingle("the register phase's descriptor must be re-registered with row permissions");

        // The whole point: these three ids can only be in the cell if RunAsync actually called the
        // read-phase judge. Deleting that call empties them and this fails.
        cell.Assertions.Should().Contain(a => a.RequirementId == Requirements.SchCatalogRetrievalReachable);
        cell.Assertions.Should().Contain(a => a.RequirementId == Requirements.SchCatalogIncludesRegisteredType);
        cell.Assertions.Should().Contain(a => a.RequirementId == Requirements.SchCatalogFieldSetMatchesDescriptor);
    }

    [Fact]
    public async Task RunAsync_ReadPhaseReportsAFailedCatalogue_TheFailureReachesTheCell()
    {
        // The negative direction of the same call site. A test that only asserts the ids are
        // PRESENT would pass against a judge that always emits passing assertions; this one pins
        // that the read phase's actual outcome is what the cell carries.
        var descriptor = Descriptor("GoAuthor", "Id", "Name");
        var runner = new ScriptedDriverRunner()
            .Script(Phase.Register, new DriverPhaseOutcome.Success("go",
                new PhaseDocument("go", "register",
                [
                    new StepResult(
                        SchemaCatalogScenario.RegisterStepName, true,
                        TypeDescriptor: DescriptorJson(descriptor))
                ])))
            .Script(Phase.Read, new DriverPhaseOutcome.Success("go",
                new PhaseDocument("go", "read",
                [
                    new StepResult(SchemaCatalogScenario.ReadStepName, false, Error: "Unavailable")
                ])));

        var cells = await DrivenScenario(runner, new RecordingReregistrar())
            .RunAsync(["go"], Context(), "acting-token");

        var cell = cells.Should().ContainSingle().Subject;
        cell.Status.Should().Be(CellStatus.Fail);
        cell.Assertions.Should().Contain(a =>
            a.RequirementId == Requirements.SchCatalogRetrievalReachable && !a.Passed);
    }

    // ── the happy path ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void JudgeCatalogue_TypePresentWithTheDeclaredFieldSet_AllAssertionsPass()
    {
        var descriptor = Descriptor("DotNetAuthor", "Id", "TenantId", "OwnerId", "Name");
        var document = ReadDocument(new StepResult(
            "get_schema", true,
            Entity: Catalogue(("DotNetAuthor", ["Id", "TenantId", "OwnerId", "Name"]))));

        var assertions = SchemaCatalogScenario.JudgeCatalogue("dotnet", descriptor, document);

        assertions.Should().OnlyContain(a => a.Passed);
        assertions.Should().Contain(a => a.RequirementId == Requirements.SchCatalogRetrievalReachable);
        assertions.Should().Contain(a => a.RequirementId == Requirements.SchCatalogIncludesRegisteredType);
        assertions.Should().Contain(a => a.RequirementId == Requirements.SchCatalogFieldSetMatchesDescriptor);
    }

    [Fact]
    public void JudgeCatalogue_CatalogueNamesTheTypeInADifferentCasing_StillMatches()
    {
        var descriptor = Descriptor("PyAuthor", "Id", "Name");
        var document = ReadDocument(new StepResult(
            "get_schema", true, Entity: Catalogue(("pyauthor", ["id", "name"]))));

        var assertions = SchemaCatalogScenario.JudgeCatalogue("python", descriptor, document);

        assertions.Should().OnlyContain(a => a.Passed);
    }

    // ── IVC-SCH-001: reachability ─────────────────────────────────────────────────────────────

    [Fact]
    public void JudgeCatalogue_DriverReportedTheStepAsFailed_FailsOnlyTheReachabilityAndDownstreamAssertions()
    {
        var descriptor = Descriptor("GoAuthor", "Id", "Name");
        var document = ReadDocument(new StepResult("get_schema", false, Error: "Unavailable"));

        var assertions = SchemaCatalogScenario.JudgeCatalogue("go", descriptor, document);

        Named(assertions, "reachable").Passed.Should().BeFalse();
        Named(assertions, "reachable").RequirementId.Should().Be(Requirements.SchCatalogRetrievalReachable);
        Named(assertions, "reachable").Detail.Should().Contain("Unavailable");
    }

    [Fact]
    public void JudgeCatalogue_DriverReportedNoReadStepAtAll_FailsReachabilityWithoutThrowing()
    {
        var descriptor = Descriptor("TsAuthor", "Id");
        var document = ReadDocument(new StepResult("something_else", true));

        var assertions = SchemaCatalogScenario.JudgeCatalogue("typescript", descriptor, document);

        Named(assertions, "reachable").Passed.Should().BeFalse();
        Named(assertions, "reachable").RequirementId.Should().Be(Requirements.SchCatalogRetrievalReachable);
    }

    // ── the backstop ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void JudgeCatalogue_DriverReportedAnEmptyCatalogue_FailsTheUncitedNonEmptyBackstop()
    {
        var descriptor = Descriptor("JavaAuthor", "Id");
        var document = ReadDocument(new StepResult("get_schema", true, Entity: Catalogue()));

        var assertions = SchemaCatalogScenario.JudgeCatalogue("java", descriptor, document);

        var backstop = Named(assertions, "non-empty schema catalogue");
        backstop.Passed.Should().BeFalse();
        backstop.RequirementId.Should().BeNull("the backstop deliberately carries no requirement ID");
    }

    [Fact]
    public void JudgeCatalogue_NoDescriptorFromTheRegisterPhase_StillFiresTheBackstopAndFailsSch002()
    {
        // The case that makes the backstop non-redundant with IVC-SCH-002: with no registered type
        // name there is nothing for SCH-002's search to look for, yet a green-but-empty cell must
        // still be impossible.
        var document = ReadDocument(new StepResult("get_schema", true, Entity: Catalogue()));

        var assertions = SchemaCatalogScenario.JudgeCatalogue("go", descriptor: null, document);

        Named(assertions, "non-empty schema catalogue").Passed.Should().BeFalse();
        var includes = assertions.Single(a => a.RequirementId == Requirements.SchCatalogIncludesRegisteredType);
        includes.Passed.Should().BeFalse();
        assertions.Should().NotContain(a => a.RequirementId == Requirements.SchCatalogFieldSetMatchesDescriptor);
    }

    // ── IVC-SCH-002: the registered type is present ───────────────────────────────────────────

    [Fact]
    public void JudgeCatalogue_CatalogueOmitsTheRegisteredType_FailsSch002AndDoesNotGradeTheFieldSet()
    {
        var descriptor = Descriptor("PyAuthor", "Id", "Name");
        var document = ReadDocument(new StepResult(
            "get_schema", true, Entity: Catalogue(("DotNetAuthor", ["Id", "Name"]))));

        var assertions = SchemaCatalogScenario.JudgeCatalogue("python", descriptor, document);

        var includes = assertions.Single(a => a.RequirementId == Requirements.SchCatalogIncludesRegisteredType);
        includes.Passed.Should().BeFalse();
        includes.Detail.Should().Contain("DotNetAuthor");
        assertions.Should().NotContain(a => a.RequirementId == Requirements.SchCatalogFieldSetMatchesDescriptor);
        Named(assertions, "non-empty schema catalogue").Passed.Should().BeTrue();
    }

    // ── IVC-SCH-003: the field set matches, in BOTH directions ────────────────────────────────

    [Fact]
    public void JudgeCatalogue_CatalogueDropsADeclaredField_FailsSch003NamingTheMissingField()
    {
        var descriptor = Descriptor("GoAuthor", "Id", "TenantId", "Name");
        var document = ReadDocument(new StepResult(
            "get_schema", true, Entity: Catalogue(("GoAuthor", ["Id", "TenantId"]))));

        var assertions = SchemaCatalogScenario.JudgeCatalogue("go", descriptor, document);

        var fieldSet = assertions.Single(a => a.RequirementId == Requirements.SchCatalogFieldSetMatchesDescriptor);
        fieldSet.Passed.Should().BeFalse();
        fieldSet.Detail.Should().Contain("declared-but-absent: [Name]");
    }

    [Fact]
    public void JudgeCatalogue_CatalogueInventsAnUndeclaredField_FailsSch003NamingTheExtraField()
    {
        // The direction a one-way subset check would silently accept.
        var descriptor = Descriptor("TsAuthor", "Id", "Name");
        var document = ReadDocument(new StepResult(
            "get_schema", true, Entity: Catalogue(("TsAuthor", ["Id", "Name", "SecretColumn"]))));

        var assertions = SchemaCatalogScenario.JudgeCatalogue("typescript", descriptor, document);

        var fieldSet = assertions.Single(a => a.RequirementId == Requirements.SchCatalogFieldSetMatchesDescriptor);
        fieldSet.Passed.Should().BeFalse();
        fieldSet.Detail.Should().Contain("catalogued-but-undeclared: [SecretColumn]");
    }

    [Fact]
    public void JudgeCatalogue_PassDetail_CountsTheFieldsTheDriverReported_NotNormalizedSetMembers()
    {
        // A driver reporting the same name in two casings sends two fields; the pass detail must
        // say two, not the one that survives Verifier.Normalize-keyed de-duplication. The pass/fail
        // condition itself is the set comparison and is deliberately unaffected.
        var descriptor = Descriptor("PyAuthor", "Id", "Name");
        var document = ReadDocument(new StepResult(
            "get_schema", true, Entity: Catalogue(("PyAuthor", ["Id", "Name", "name"]))));

        var assertions = SchemaCatalogScenario.JudgeCatalogue("python", descriptor, document);

        var fieldSet = assertions.Single(a => a.RequirementId == Requirements.SchCatalogFieldSetMatchesDescriptor);
        fieldSet.Passed.Should().BeTrue();
        fieldSet.Detail.Should().Contain("3 field(s)");
    }

    // ── ReadTypes: malformed reports are data, never exceptions ───────────────────────────────

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("{}")]
    [InlineData("{\"types\": {}}")]
    [InlineData("{\"types\": [1, 2]}")]
    [InlineData("{\"types\": [{\"fields\": []}]}")]
    public void ReadTypes_MalformedOrEmptyReport_YieldsNoTypesRatherThanThrowing(string json)
    {
        var element = JsonDocument.Parse(json).RootElement;

        SchemaCatalogScenario.ReadTypes(element).Should().BeEmpty();
    }

    [Fact]
    public void ReadTypes_NullEntity_YieldsNoTypes() =>
        SchemaCatalogScenario.ReadTypes(null).Should().BeEmpty();

    [Fact]
    public void ReadTypes_FieldEntriesWithoutANameAreDropped_TheTypeItselfSurvives()
    {
        var element = JsonDocument.Parse(
            """{"types":[{"name":"A","fields":[{"name":"Id"},{},{"name":""}]}]}""").RootElement;

        var types = SchemaCatalogScenario.ReadTypes(element);

        types.Should().ContainSingle();
        types[0].FieldNames.Should().Equal("Id");
    }

    // ── RunAsync plumbing ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_NoLanguagesRequested_ReturnsNoCells() =>
        (await BuildScenario().RunAsync([], Context(), "acting-token")).Should().BeEmpty();

    [Fact]
    public async Task RunAsync_EveryDriverBreaksDuringRegister_NoCellIsGreen()
    {
        // A missing toolchain legitimately yields Skip rather than Fail (the harness's one
        // sanctioned skip), so the invariant under test is that nothing goes GREEN — not that
        // every cell is red.
        var cells = await BuildScenario().RunAsync(["dotnet", "python"], Context(), "acting-token");

        cells.Should().HaveCount(2);
        cells.Should().NotContain(c => c.Status == CellStatus.Ok);
        cells.Should().OnlyContain(c => c.Scenario == SchemaCatalogScenario.Name);
    }

    [Fact]
    public async Task RunAsync_UnrecognizedLanguage_FailsThatRowRatherThanSilentlyDroppingIt()
    {
        var cells = await BuildScenario().RunAsync(["rust"], Context(), "acting-token");

        cells.Should().ContainSingle()
            .Which.Detail.Should().Contain("not a recognized conformance driver language");
    }

    // ── Ruling 31 / Important 1: the whole SCH axis reaches a cell ─────────────────────────────

    /// <summary>
    /// The SCH axis has exactly three requirements and ALL FIVE of their citations live inside
    /// <see cref="SchemaCatalogScenario.JudgeCatalogue"/>, reached from exactly one place — the
    /// read-phase loop, now <see cref="SchemaCatalogScenario.JudgeReadPhase"/>. Delete that call
    /// and every SCH const is still cited in source, so the coverage gate's Check2 stays green
    /// while the entire axis grades nothing anywhere in the matrix. This test is what fails
    /// instead.
    ///
    /// <para>This pins a WIRING claim, not a grading claim: that the judgement reaches a report
    /// CELL. What the assertions decide is judged by the JudgeCatalogue tests above.</para>
    /// </summary>
    [Fact]
    public void JudgeReadPhase_TheCatalogueJudgement_ReachesTheCellCarryingEverySchCitation()
    {
        var state = new SchemaCatalogScenario.LanguageState
        {
            Descriptor = Descriptor("DotNetAuthor", "Id", "Name"),
        };
        var document = ReadDocument(new StepResult(
            "get_schema", true, Entity: Catalogue(("DotNetAuthor", ["Id", "Name"]))));

        SchemaCatalogScenario.JudgeReadPhase("dotnet", state, document);

        var cell = ScenarioCells.Cell("dotnet", SchemaCatalogScenario.Name, state);

        cell.Assertions.Select(a => a.RequirementId).Should().Contain(
        [
            Requirements.SchCatalogRetrievalReachable,
            Requirements.SchCatalogIncludesRegisteredType,
            Requirements.SchCatalogFieldSetMatchesDescriptor,
        ], "a citation that exists in source but never executes grades nothing — every SCH "
         + "requirement JudgeCatalogue constructs must reach the cell");
    }
}
