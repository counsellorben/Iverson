using FluentAssertions;
using Grpc.Net.Client;
using Iverson.Client.Contracts;
using Iverson.ClientConformance.Scenarios;
using Xunit;

namespace Iverson.ClientConformance.Tests;

public class InteropScenarioTests
{
    private static DriverContext Context() => new(
        Scenario: InteropScenario.Name,
        Type: string.Empty,
        Tenant: "iverson-loadtest-dynamic",
        GrpcUrl: "http://localhost:5000",
        ClientId: "client-id",
        ClientSecret: "client-secret",
        TokenEndpoint: "http://localhost:9000/application/o/token/",
        ActingToken: "acting-token",
        OwnerId: "owner-id",
        IdPrefix: "s4-");

    /// <summary>
    /// Builds a scenario whose Reregistrar is never actually dialed in the tests below: a
    /// register-phase failure returns before any re-registration call is attempted. The channel
    /// only needs to construct, not connect.
    /// </summary>
    private static InteropScenario BuildScenario(string repoRoot = "/tmp")
    {
        var channel = GrpcChannel.ForAddress("http://localhost:1");
        var mapping = new ObjectMappingService.ObjectMappingServiceClient(channel);
        return new InteropScenario(new DriverRunner(repoRoot: repoRoot), new Reregistrar(mapping));
    }

    private static PhaseDocument ReadDocument(params StepResult[] steps) =>
        new("dotnet", "read", steps);

    // ── RunAsync: the register-once rule is the load-bearing, easy-to-get-wrong piece (see the
    // class doc comment). These exercise RunAsync itself — not just a pure helper — through the
    // real DriverRunner, the same seam CrudRoundtripScenarioTests and NamingRejectedScenarioTests
    // use to reach a controlled failure without a live stack: repoRoot "/tmp" has no
    // Iverson.Client.Conformance.Driver.csproj, so `dotnet build` fails loudly and predictably.

    [Fact]
    public async Task RunAsync_NoLanguagesRequested_ReturnsNoCells()
    {
        var scenario = BuildScenario();

        var cells = await scenario.RunAsync([], Context(), actingToken: "acting-token");

        cells.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_RegisterPhaseDriverBreaks_EveryRequestedLanguageFails_NamingTheSharedCause()
    {
        // Only .NET ever runs interop's register phase (register-once rule) — a failure there is
        // not one language's problem, it is every requested language's, since none of them can
        // write without the orchestrator's one-time re-registration succeeding first. The
        // regression this guards against is a version that only failed the "dotnet" cell (or
        // silently dropped the other languages) instead of reporting the shared cause on all of
        // them.
        var scenario = BuildScenario();

        var cells = await scenario.RunAsync(
            ["python", "java", "dotnet", "go", "typescript"], Context(), actingToken: "acting-token");

        cells.Should().HaveCount(5);
        cells.Should().OnlyContain(c => c.Status == CellStatus.Fail);
        cells.Should().OnlyContain(c =>
            c.Detail != null && c.Detail.Contains("register phase") && c.Detail.Contains("dotnet"));
    }

    [Fact]
    public async Task RunAsync_RegisterPhaseDriverBreaks_DotnetNotRequested_StillFails_NamingDotnetAsCause()
    {
        // The register phase always runs against "dotnet" regardless of --languages — it is pure
        // infrastructure for the descriptor, not a per-language row. Requesting only non-dotnet
        // languages must still surface the same shared failure for each of them.
        var scenario = BuildScenario();

        var cells = await scenario.RunAsync(["go", "python"], Context(), actingToken: "acting-token");

        cells.Should().HaveCount(2);
        cells.Should().OnlyContain(c => c.Language == "go" || c.Language == "python");
        cells.Should().OnlyContain(c => c.Status == CellStatus.Fail);
        cells.Should().OnlyContain(c => c.Detail != null && c.Detail.Contains("'dotnet'"));
    }

    // ── TryCaptureDescriptors: the register-phase document parsing, pure and unit-testable
    // exactly like NavPropertyRejectedScenario.Judge.

    [Fact]
    public void TryCaptureDescriptors_BothStepsOkWithDescriptors_ReturnsBoth_NoFailure()
    {
        var authorJson = System.Text.Json.JsonDocument.Parse(
            """{"typeName":"SharedAuthor","properties":[{"name":"Id","isKey":true}]}""").RootElement;
        var articleJson = System.Text.Json.JsonDocument.Parse(
            """{"typeName":"SharedArticle","properties":[{"name":"Id","isKey":true}]}""").RootElement;

        var document = new PhaseDocument("dotnet", "register",
        [
            new StepResult("register_shared_author", Ok: true, TypeDescriptor: authorJson),
            new StepResult("register_shared_article", Ok: true, TypeDescriptor: articleJson),
        ]);

        var (author, article, failure) = InteropScenario.TryCaptureDescriptors(document);

        failure.Should().BeNull();
        author.Should().NotBeNull();
        article.Should().NotBeNull();
        author!.Descriptor.TypeName.Should().Be("SharedAuthor");
        article!.Descriptor.TypeName.Should().Be("SharedArticle");
    }

    [Fact]
    public void TryCaptureDescriptors_MissingStep_ReturnsFailure_NamingWhichSteps()
    {
        var document = new PhaseDocument("dotnet", "register",
        [
            new StepResult("register_shared_author", Ok: true),
        ]);

        var (author, article, failure) = InteropScenario.TryCaptureDescriptors(document);

        author.Should().BeNull();
        article.Should().BeNull();
        failure.Should().NotBeNull();
        failure.Should().Contain("register_shared_author").And.Contain("register_shared_article");
    }

    [Fact]
    public void TryCaptureDescriptors_StepFailed_ReturnsTheDriversOwnErrorText()
    {
        var document = new PhaseDocument("dotnet", "register",
        [
            new StepResult("register_shared_author", Ok: false, Error: "PermissionDenied: schema_admin required"),
            new StepResult("register_shared_article", Ok: false, Error: "PermissionDenied: schema_admin required"),
        ]);

        var (author, article, failure) = InteropScenario.TryCaptureDescriptors(document);

        author.Should().BeNull();
        article.Should().BeNull();
        failure.Should().Contain("schema_admin");
    }

    // ── JudgeAgreement: the twenty-five-read agreement check itself, pure and unit-testable.

    [Fact]
    public void JudgeAgreement_AllFiveReadersReportTheSameForeignKey_AllPass()
    {
        var sharedAuthorId = Guid.NewGuid();
        var readers = new[] { "dotnet", "go", "java", "python", "typescript" };
        var readDocuments = readers.ToDictionary(r => r, r => ReadDocument(
            new StepResult("read_shared_article_dotnet", Ok: true, Entity: EntityWithFk(sharedAuthorId))));

        var results = InteropScenario.JudgeAgreement("dotnet", readers, readDocuments);

        results.Should().HaveCount(5);
        results.Select(r => r.Assertion).Should().OnlyContain(a => a.Passed);
    }

    [Fact]
    public void JudgeAgreement_OneReaderDisagrees_OnlyThatReadersAssertionFails()
    {
        // The exact regression this scenario exists to catch: two client libraries reporting
        // different foreign keys for the SAME row a third language wrote.
        var canonicalAuthorId = Guid.NewGuid();
        var wrongAuthorId = Guid.NewGuid();

        var readDocuments = new Dictionary<string, PhaseDocument>
        {
            ["dotnet"] = ReadDocument(new StepResult(
                "read_shared_article_go", Ok: true, Entity: EntityWithFk(canonicalAuthorId))),
            ["java"] = ReadDocument(new StepResult(
                "read_shared_article_go", Ok: true, Entity: EntityWithFk(wrongAuthorId))),
        };

        var results = InteropScenario.JudgeAgreement("go", ["dotnet", "java"], readDocuments);

        results.Should().HaveCount(2);
        results.Single(r => r.Reader == "dotnet").Assertion.Passed.Should().BeTrue();
        var javaResult = results.Single(r => r.Reader == "java");
        javaResult.Assertion.Passed.Should().BeFalse();
        javaResult.Assertion.Detail.Should().Contain("reader=java");
    }

    [Fact]
    public void JudgeAgreement_ReaderReportsNoSuchStep_FailsNamingTheMissingStep()
    {
        var readDocuments = new Dictionary<string, PhaseDocument>
        {
            ["dotnet"] = ReadDocument(new StepResult(
                "read_shared_article_python", Ok: true, Entity: EntityWithFk(Guid.NewGuid()))),
            ["go"] = ReadDocument(), // never read python's row at all
        };

        var results = InteropScenario.JudgeAgreement("python", ["dotnet", "go"], readDocuments);

        var goResult = results.Single(r => r.Reader == "go");
        goResult.Assertion.Passed.Should().BeFalse();
        goResult.Assertion.Detail.Should().Contain("no such read step");
    }

    [Fact]
    public void JudgeAgreement_ReaderStepFailed_FailsWithTheDriversOwnErrorText()
    {
        var readDocuments = new Dictionary<string, PhaseDocument>
        {
            ["dotnet"] = ReadDocument(new StepResult(
                "read_shared_article_java", Ok: false, Error: "NotFound: no such row")),
        };

        var results = InteropScenario.JudgeAgreement("java", ["dotnet"], readDocuments);

        results.Should().ContainSingle();
        results[0].Assertion.Passed.Should().BeFalse();
        results[0].Assertion.Detail.Should().Contain("NotFound: no such row");
    }

    private static System.Text.Json.JsonElement EntityWithFk(Guid sharedAuthorId) =>
        System.Text.Json.JsonDocument.Parse(
            $$"""{"Title":"x","SharedAuthorId":"{{sharedAuthorId}}"}""").RootElement;
}
