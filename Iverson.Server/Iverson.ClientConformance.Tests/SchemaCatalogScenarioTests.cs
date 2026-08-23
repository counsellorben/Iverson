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

    private static Assertion Named(IReadOnlyList<Assertion> assertions, string fragment) =>
        assertions.Single(a => a.Name.Contains(fragment, StringComparison.Ordinal));

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
}
