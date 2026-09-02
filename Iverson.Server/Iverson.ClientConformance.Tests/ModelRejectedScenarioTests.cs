using System.Text.Json;
using FluentAssertions;
using Google.Protobuf;
using Grpc.Core;
using Iverson.Client.Contracts;
using Iverson.ClientConformance.Scenarios;
using Iverson.Embeddings;
using Xunit;

namespace Iverson.ClientConformance.Tests;

/// <summary>
/// Unit coverage for S11 <c>model-rejected</c>. The judgement halves
/// (<see cref="ModelRejectedScenario.JudgeRejection"/>, <see cref="ModelRejectedScenario.JudgeParity"/>)
/// are pure over reported data and graded directly; <c>RunAsync</c> is driven END TO END through
/// <see cref="ScriptedDriverRunner"/> and <see cref="RecordingReregistrar"/>, which is what makes
/// the WIRING — the calls that carry those judgements into report cells, and the re-registration
/// the whole scenario turns on — observable at all. Deleting either judgement call from
/// <c>RunAsync</c> leaves the pure tests green and the scenario grading nothing.
/// </summary>
public class ModelRejectedScenarioTests
{
    private const string StoredModel = "nomic-embed-text";

    private static DriverContext Context() => new(
        Scenario: ModelRejectedScenario.Name,
        Type: string.Empty,
        Tenant: "iverson-loadtest-dynamic",
        GrpcUrl: "http://localhost:5000",
        ClientId: "client-id",
        ClientSecret: "client-secret",
        TokenEndpoint: "http://localhost:9000/application/o/token/",
        ActingToken: "acting-token",
        OwnerId: "owner-id",
        IdPrefix: "s11-");

    /// <summary>
    /// The guard's message, spelled here as the harness's OWN copy of what
    /// <c>SchemaRegistrationOrchestrator</c> produces — the same reason
    /// <see cref="PostgresProbe.TableName"/> is a copy. Fixtures built off the server's own
    /// formatting could not catch the server rewording it; this one can, and the collection-naming
    /// fixture below is exactly that case.
    /// </summary>
    private static string GuardMessage(string typeName, string priorModel, string nextModel)
    {
        var collectionBase = PostgresProbe.TableName(typeName);
        return $"Type '{typeName}' is registered with embedding model '{priorModel}', but this "
             + $"registration resolves to '{nextModel}'. Changing a type's model would leave one "
             + $"collection holding vectors from two incompatible spaces, which no dimension check "
             + $"catches when the two models share a dimension. To change it, BOTH clear the schema "
             + $"row and drop the collections: "
             + $"DELETE FROM _iverson_schema WHERE type_name = '{typeName}'; "
             + $"then, for every tenant that has ingested '{typeName}', drop Qdrant "
             + $"collections '{collectionBase}_<tenantId>' (vectors) and "
             + $"'{collectionBase}_chunks_<tenantId>' (chunks). "
             + $"Dropping the collections alone leaves this row, and the next registration is "
             + $"rejected identically. Until then, '{priorModel}' must remain pulled in this "
             + $"deployment's Ollama — every other type still registered under it needs it to "
             + $"stay reachable.";
    }

    private static RpcException Rejected(string message, StatusCode code = StatusCode.FailedPrecondition) =>
        new(new Status(code, message));

    private static JsonElement DescriptorJson(string typeName) =>
        JsonDocument.Parse(JsonFormatter.Default.Format(new TypeDescriptor
        {
            TypeName = typeName,
            Properties =
            {
                new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true },
                new PropertyDescriptor
                {
                    Name = "Title", ClrType = ClrType.ClrString,
                    IsEmbedding = true, ModelId = StoredModel, VectorDim = 768,
                },
            },
        })).RootElement.Clone();

    private static DriverPhaseOutcome.Success Registered(string language, string? typeName = null) =>
        new(language, new PhaseDocument(language, "register",
        [
            new StepResult(
                ModelRejectedScenario.RegisterStepName, true,
                TypeDescriptor: DescriptorJson(typeName ?? ModelRejectedScenario.TypeNameFor(language))),
        ]));

    /// <summary>
    /// The probe's connection string is empty on purpose, exactly as
    /// <c>IdentityScenarioTests</c> builds its <c>PostgresProbe</c>: every stored-model read fails
    /// loudly and locally, so the wiring tests run with no stack at all. A test that needs a
    /// SUCCESSFUL observation supplies it to <see cref="ModelRejectedScenario.JudgeParity"/> or
    /// <see cref="ModelRejectedScenario.JudgeRejection"/> as a value instead — which is precisely
    /// why both take the observation rather than fetching it.
    /// </summary>
    private static ModelRejectedScenario Scenario(
        ScriptedDriverRunner runner, RecordingReregistrar reregistrar) =>
        new(runner, reregistrar, new SchemaProbe(string.Empty));

    private static Assertion Named(IEnumerable<Assertion> assertions, string fragment) =>
        assertions.Single(a => a.Name.Contains(fragment, StringComparison.Ordinal));

    // ── the rejection judgement ───────────────────────────────────────────────────────────────

    [Fact]
    public void JudgeRejection_TheServerAcceptedTheModelChange_FailsCitingReg006()
    {
        var assertions = ModelRejectedScenario.JudgeRejection("dotnet", "S11ModelDotnet", StoredModel, caught: null);

        var only = assertions.Should().ContainSingle().Subject;
        only.Passed.Should().BeFalse();
        only.RequirementId.Should().Be(Requirements.RegEmbeddingModelChangeRejected);
        only.Detail.Should().Contain("two incompatible spaces");
    }

    [Fact]
    public void JudgeRejection_TheGuardsRealMessage_PassesEveryArm()
    {
        var assertions = ModelRejectedScenario.JudgeRejection(
            "dotnet", "S11ModelDotnet", StoredModel,
            Rejected(GuardMessage("S11ModelDotnet", StoredModel, ModelRejectedScenario.OverrideModelId)));

        assertions.Should().OnlyContain(a => a.Passed);
        assertions.Should().HaveCount(6, "the rejection, its status code, both models and both halves of the remedy");
    }

    [Fact]
    public void JudgeRejection_EveryArm_CitesReg006()
    {
        var assertions = ModelRejectedScenario.JudgeRejection(
            "dotnet", "S11ModelDotnet", StoredModel,
            Rejected(GuardMessage("S11ModelDotnet", StoredModel, ModelRejectedScenario.OverrideModelId)));

        assertions.Should().OnlyContain(a => a.RequirementId == Requirements.RegEmbeddingModelChangeRejected);
    }

    [Fact]
    public void JudgeRejection_RejectedWithInvalidArgument_FailsOnlyTheStatusArm()
    {
        var assertions = ModelRejectedScenario.JudgeRejection(
            "dotnet", "S11ModelDotnet", StoredModel,
            Rejected(GuardMessage("S11ModelDotnet", StoredModel, ModelRejectedScenario.OverrideModelId),
                StatusCode.InvalidArgument));

        Named(assertions, "rejected with FailedPrecondition").Passed.Should().BeFalse();
        assertions.Where(a => !a.Name.Contains("FailedPrecondition", StringComparison.Ordinal))
            .Should().OnlyContain(a => a.Passed);
    }

    /// <summary>
    /// THE fixture this scenario's collection arm exists for. The guard's earlier wording named the
    /// bare collection base — <c>s11_model_dotnets</c> — which is never a real Qdrant collection:
    /// every collection is tenant-qualified, so an operator following that message searches for
    /// something that has never existed, concludes the cleanup is already done, and leaves every
    /// real per-tenant collection holding the mixed vectors. An assertion loose enough to accept
    /// both wordings would not have caught the regression back to it.
    /// </summary>
    [Fact]
    public void JudgeRejection_TheOldBareCollectionWording_FailsTheCollectionArm_AndOnlyThatArm()
    {
        const string typeName = "S11ModelDotnet";
        var collectionBase = PostgresProbe.TableName(typeName);
        var message = GuardMessage(typeName, StoredModel, ModelRejectedScenario.OverrideModelId)
            .Replace($"'{collectionBase}_<tenantId>'", $"'{collectionBase}'", StringComparison.Ordinal)
            .Replace($"'{collectionBase}_chunks_<tenantId>'", $"'{collectionBase}_chunks'", StringComparison.Ordinal);

        var assertions = ModelRejectedScenario.JudgeRejection("dotnet", typeName, StoredModel, Rejected(message));

        Named(assertions, "tenant-qualified Qdrant collections").Passed.Should().BeFalse();
        Named(assertions, "the schema row to clear").Passed.Should().BeTrue(
            "the two halves of the remedy are graded independently");
    }

    [Fact]
    public void JudgeRejection_MessageOmittingTheDeleteStatement_FailsOnlyTheSchemaRowArm()
    {
        const string typeName = "S11ModelDotnet";
        var message = GuardMessage(typeName, StoredModel, ModelRejectedScenario.OverrideModelId)
            .Replace($"DELETE FROM _iverson_schema WHERE type_name = '{typeName}'",
                "clear the registry row", StringComparison.Ordinal);

        var assertions = ModelRejectedScenario.JudgeRejection("dotnet", typeName, StoredModel, Rejected(message));

        Named(assertions, "the schema row to clear").Passed.Should().BeFalse();
        Named(assertions, "tenant-qualified Qdrant collections").Passed.Should().BeTrue();
    }

    /// <summary>
    /// The stored-model arm compares the server's claim against the schema probe's INDEPENDENT
    /// reading, so a message naming some other model — a guard reading the wrong descriptor, say —
    /// reddens rather than passing on "some model was named".
    /// </summary>
    [Fact]
    public void JudgeRejection_MessageNamesAModelTheRowDoesNotCarry_FailsThatArm()
    {
        var assertions = ModelRejectedScenario.JudgeRejection(
            "dotnet", "S11ModelDotnet", StoredModel,
            Rejected(GuardMessage("S11ModelDotnet", "some-other-stored-model", ModelRejectedScenario.OverrideModelId)));

        Named(assertions, "the model 'S11ModelDotnet's registered schema carries").Passed.Should().BeFalse();
        Named(assertions, "the model this registration resolved to").Passed.Should().BeTrue();
    }

    [Fact]
    public void JudgeRejection_TheProbeReadNoStoredModel_FailsThatArmNamingWhy()
    {
        var assertions = ModelRejectedScenario.JudgeRejection(
            "dotnet", "S11ModelDotnet", priorModel: null,
            Rejected(GuardMessage("S11ModelDotnet", StoredModel, ModelRejectedScenario.OverrideModelId)));

        var arm = Named(assertions, "registered schema carries");
        arm.Passed.Should().BeFalse();
        arm.Detail.Should().Contain("the schema probe read no model");
    }

    [Fact]
    public void JudgeRejection_MessageNamingADifferentResolvedModel_FailsThatArm()
    {
        var assertions = ModelRejectedScenario.JudgeRejection(
            "dotnet", "S11ModelDotnet", StoredModel,
            Rejected(GuardMessage("S11ModelDotnet", StoredModel, "a-third-model")));

        Named(assertions, "the model this registration resolved to").Passed.Should().BeFalse();
    }

    // ── the parity judgement ──────────────────────────────────────────────────────────────────

    private static ModelRejectedScenario.ModelObservation Read(string language, string? model) =>
        new(language, ModelRejectedScenario.TypeNameFor(language), model, null);

    [Fact]
    public void JudgeParity_EveryFixtureCarriesTheSameModel_PassesForEveryLanguage()
    {
        var judged = ModelRejectedScenario.JudgeParity(
            new[] { "dotnet", "python", "typescript", "go", "java" }.Select(l => Read(l, StoredModel)).ToList());

        judged.Should().HaveCount(5);
        judged.Should().OnlyContain(j => j.Assertion.Passed);
        judged.Should().OnlyContain(j => j.Assertion.RequirementId == Requirements.RegEmbeddingModelChangeRejected);
    }

    /// <summary>
    /// Parity is a joint property, so a disagreement reddens EVERY column and every cell's detail
    /// names every observation. Picking one language's value as the reference would render a
    /// two-way disagreement as one green column and one red one, attributing the defect to
    /// whichever language happened not to be chosen.
    /// </summary>
    [Fact]
    public void JudgeParity_OneFixtureDiverges_FailsEveryLanguage_AndNamesEveryObservation()
    {
        var judged = ModelRejectedScenario.JudgeParity(
        [
            Read("dotnet", StoredModel),
            Read("python", StoredModel),
            Read("go", "arctic-embed"),
        ]);

        judged.Should().OnlyContain(j => !j.Assertion.Passed);
        judged.Should().OnlyContain(j => j.Assertion.Detail.Contains("arctic-embed", StringComparison.Ordinal));
        judged.Should().OnlyContain(j => j.Assertion.Detail.Contains("S11ModelGo", StringComparison.Ordinal));
    }

    [Fact]
    public void JudgeParity_AProbeFailure_FailsAndCarriesTheProbeReason()
    {
        var judged = ModelRejectedScenario.JudgeParity(
        [
            Read("dotnet", StoredModel),
            new ModelRejectedScenario.ModelObservation("go", "S11ModelGo", null, "NpgsqlException: down"),
        ]);

        judged.Should().OnlyContain(j => !j.Assertion.Passed);
        judged.Should().OnlyContain(j => j.Assertion.Detail.Contains("the schema probe failed", StringComparison.Ordinal));
    }

    /// <summary>
    /// A row with no embedding model at all is NOT parity. It is the state a fixture that lost its
    /// <c>[IversonEmbedding]</c> declaration would be in — every rejection arm would then be graded
    /// against a type the guard can never fire for.
    /// </summary>
    [Fact]
    public void JudgeParity_AFixtureCarryingNoModelAtAll_Fails()
    {
        var judged = ModelRejectedScenario.JudgeParity([Read("dotnet", StoredModel), Read("go", null)]);

        judged.Should().OnlyContain(j => !j.Assertion.Passed);
        judged.Should().OnlyContain(j =>
            j.Assertion.Detail.Contains("no schema row carrying an embedding model", StringComparison.Ordinal));
    }

    [Fact]
    public void JudgeParity_NoObservationsAtAll_GradesNothing()
    {
        ModelRejectedScenario.JudgeParity([]).Should().BeEmpty();
    }

    // ── RunAsync, driven end to end ───────────────────────────────────────────────────────────

    /// <summary>
    /// THE wiring mutation: dropping the <c>JudgeRejection</c> call from <c>RunAsync</c>. Every
    /// pure test above stays green through it while the scenario grades no rejection at all.
    /// </summary>
    [Fact]
    public async Task RunAsync_TheServerRejectsTheModelChange_TheRejectionJudgementReachesTheCell()
    {
        var runner = new ScriptedDriverRunner().Script(Phase.Register, Registered("dotnet"));
        var reregistrar = new RecordingReregistrar
        {
            Throws = Rejected(GuardMessage(
                ModelRejectedScenario.TypeNameFor("dotnet"), StoredModel, ModelRejectedScenario.OverrideModelId)),
        };

        var cell = (await Scenario(runner, reregistrar).RunAsync(["dotnet"], Context(), "acting-token"))
            .Should().ContainSingle().Subject;

        cell.Scenario.Should().Be(ModelRejectedScenario.Name);
        Named(cell.Assertions, "rejects a re-registration").Passed.Should().BeTrue();
        Named(cell.Assertions, "rejected with FailedPrecondition").Passed.Should().BeTrue();
        Named(cell.Assertions, "tenant-qualified Qdrant collections").Passed.Should().BeTrue();
    }

    /// <summary>
    /// The second wiring mutation: dropping the <c>JudgeParity</c> call. The probe is unusable
    /// here, so the parity assertion must reach the cell FAILING and naming the probe failure —
    /// the one thing a scenario that silently skipped the read could not produce.
    /// </summary>
    [Fact]
    public async Task RunAsync_TheParityJudgementReachesTheCell_CarryingTheProbeFailure()
    {
        var runner = new ScriptedDriverRunner().Script(Phase.Register, Registered("dotnet"));

        var cell = (await Scenario(runner, new RecordingReregistrar()).RunAsync(["dotnet"], Context(), "acting-token"))
            .Should().ContainSingle().Subject;

        var parity = Named(cell.Assertions, "carries one embedding model");
        parity.Passed.Should().BeFalse();
        parity.RequirementId.Should().Be(Requirements.RegEmbeddingModelChangeRejected);
        parity.Detail.Should().Contain("the schema probe failed");
    }

    /// <summary>
    /// The re-registration itself: each language's OWN fixture, each carrying the model override.
    /// A scenario that re-registered without an override would provoke nothing on a live stack
    /// while every scripted-rejection assertion above stayed green.
    /// </summary>
    [Fact]
    public async Task RunAsync_ReregistersEachLanguagesOwnFixture_WithTheModelOverride()
    {
        var runner = new ScriptedDriverRunner()
            .Script(Phase.Register, Registered("dotnet"), Registered("go"));
        var reregistrar = new RecordingReregistrar();

        await Scenario(runner, reregistrar).RunAsync(["dotnet", "go"], Context(), "acting-token");

        reregistrar.Calls.Should().HaveCount(2);
        reregistrar.Calls.Should().OnlyContain(c => c.ModelId == ModelRejectedScenario.OverrideModelId);
        reregistrar.Calls.Select(c => c.TypeName).Should().BeEquivalentTo(["S11ModelDotnet", "S11ModelGo"]);
        reregistrar.Calls.Should().OnlyContain(c => c.ActingToken == "acting-token");
    }

    [Fact]
    public async Task RunAsync_TheDriverToolchainIsAbsent_RendersASkipAndNeverReregisters()
    {
        var runner = new ScriptedDriverRunner()
            .Script(Phase.Register, new DriverPhaseOutcome.Skipped("go", "skip (go not found)"));
        var reregistrar = new RecordingReregistrar();

        var cell = (await Scenario(runner, reregistrar).RunAsync(["go"], Context(), "acting-token"))
            .Should().ContainSingle().Subject;

        cell.Status.Should().Be(CellStatus.Skip);
        cell.Reason.Should().Be("skip (go not found)");
        reregistrar.Calls.Should().BeEmpty("a language that never registered a fixture has nothing to re-register");
    }

    [Fact]
    public async Task RunAsync_TheDriverBroke_FailsThatColumnNamingTheExitCode_AndNeverReregisters()
    {
        var runner = new ScriptedDriverRunner()
            .Script(Phase.Register, new DriverPhaseOutcome.Broken("java", 2, "unsupported scenario 'model-rejected'"));
        var reregistrar = new RecordingReregistrar();

        var cell = (await Scenario(runner, reregistrar).RunAsync(["java"], Context(), "acting-token"))
            .Should().ContainSingle().Subject;

        cell.Status.Should().Be(CellStatus.Fail);
        cell.Detail.Should().Contain("exit 2").And.Contain("unsupported scenario");
        reregistrar.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_TheDriverReportedNoRegisterStep_FailsThatColumnWithoutReregistering()
    {
        var runner = new ScriptedDriverRunner().Script(Phase.Register,
            new DriverPhaseOutcome.Success("python",
                new PhaseDocument("python", "register", [new StepResult("something_else", true)])));
        var reregistrar = new RecordingReregistrar();

        var cell = (await Scenario(runner, reregistrar).RunAsync(["python"], Context(), "acting-token"))
            .Should().ContainSingle().Subject;

        cell.Status.Should().Be(CellStatus.Fail);
        Named(cell.Assertions, "reported the fixture it registered").Detail.Should()
            .Contain(ModelRejectedScenario.RegisterStepName);
        reregistrar.Calls.Should().BeEmpty();
    }

    /// <summary>
    /// The driver-contract arm: a driver registering some OTHER type would leave the probe reading
    /// one row and the rejection provoked against another, and every downstream assertion would
    /// then be about a type this scenario never asked for.
    /// </summary>
    [Fact]
    public async Task RunAsync_TheDriverRegisteredADifferentType_RedsTheFixtureContractAssertion()
    {
        var runner = new ScriptedDriverRunner()
            .Script(Phase.Register, Registered("dotnet", typeName: "VectorDoc"));

        var cell = (await Scenario(runner, new RecordingReregistrar()).RunAsync(["dotnet"], Context(), "acting-token"))
            .Should().ContainSingle().Subject;

        var contract = Named(cell.Assertions, "registered this scenario's fixture type");
        contract.Passed.Should().BeFalse();
        contract.Detail.Should().Contain("VectorDoc");
    }

    /// <summary>
    /// A re-registration failing with something that is NOT a gRPC status is the harness's own
    /// break, not an observation of the server. It must redden this one column and let the run
    /// continue — escaping would reach <c>Program.cs</c>'s outer catch and discard every cell every
    /// other scenario had already collected.
    /// </summary>
    [Fact]
    public async Task RunAsync_TheReregistrationBrokeOutsideGrpc_FailsThatColumnWithoutEscaping()
    {
        var runner = new ScriptedDriverRunner().Script(Phase.Register, Registered("dotnet"));
        var reregistrar = new RecordingReregistrar { Throws = new InvalidOperationException("channel disposed") };

        var cell = (await Scenario(runner, reregistrar).RunAsync(["dotnet"], Context(), "acting-token"))
            .Should().ContainSingle().Subject;

        cell.Status.Should().Be(CellStatus.Fail);
        var arm = Named(cell.Assertions, "could re-register");
        arm.Passed.Should().BeFalse();
        arm.Detail.Should().Contain("InvalidOperationException").And.Contain("channel disposed");
        cell.Assertions.Should().NotContain(a => a.Name.Contains("rejects a re-registration", StringComparison.Ordinal),
            "an unobserved re-registration must not be graded as though the server had answered");
    }

    [Fact]
    public async Task RunAsync_ALanguageTheRunnerDoesNotRecognize_FailsThatColumn()
    {
        var runner = new ScriptedDriverRunner().Script(Phase.Register, Registered("dotnet"));

        var cells = await Scenario(runner, new RecordingReregistrar())
            .RunAsync(["dotnet", "rust"], Context(), "acting-token");

        var rust = cells.Single(c => c.Language == "rust");
        rust.Status.Should().Be(CellStatus.Fail);
        rust.Detail.Should().Contain("not a recognized conformance driver language");
    }

    [Fact]
    public async Task RunAsync_NoLanguagesRequested_RunsNothingAndReturnsNoCells()
    {
        var runner = new ScriptedDriverRunner();

        var cells = await Scenario(runner, new RecordingReregistrar()).RunAsync([], Context(), "acting-token");

        cells.Should().BeEmpty();
        runner.Calls.Should().BeEmpty();
    }

    // ── the driver contract T6-T10 implement against ──────────────────────────────────────────

    /// <summary>
    /// One fixture per language, never shared, and each a legal server identifier:
    /// <c>SchemaRegistrationOrchestrator</c>'s pattern is <c>^[A-Za-z][A-Za-z0-9]*$</c>, so a name
    /// carrying an underscore or a hyphen is rejected at registration and every arm of this
    /// scenario would fail for the wrong reason.
    /// </summary>
    [Theory]
    [InlineData("dotnet", "S11ModelDotnet")]
    [InlineData("python", "S11ModelPython")]
    [InlineData("typescript", "S11ModelTypescript")]
    [InlineData("go", "S11ModelGo")]
    [InlineData("java", "S11ModelJava")]
    public void TypeNameFor_IsAPerLanguageLegalIdentifier(string language, string expected)
    {
        ModelRejectedScenario.TypeNameFor(language).Should().Be(expected);
        ModelRejectedScenario.TypeNameFor(language).Should().MatchRegex("^[A-Za-z][A-Za-z0-9]*$");
    }

    /// <summary>
    /// The override must be a model no deployment can hold. If it ever coincided with the model the
    /// server resolves for these fixtures, the guard's two models would compare equal, nothing
    /// would be rejected, and the scenario would report a real regression as five green cells —
    /// with the failure indistinguishable from a genuinely broken guard.
    ///
    /// <para>Graded against the two model names the SYSTEM knows, not against the constant's own
    /// spelling: <c>EmbeddingServiceOptions.ModelId</c>'s default is what an unconfigured
    /// deployment resolves to, and <c>EmbeddingPrefixes.Table</c> is the closed set of model
    /// FAMILIES this build has shipped support for — the ones a deployment is actually likely to be
    /// pointed at. Family, not full id, because Ollama ids carry tags
    /// (<c>nomic-embed-text:latest</c>), so an equality check alone would pass for a real model
    /// merely because it was tagged.</para>
    ///
    /// <para>An earlier version of this test asserted <c>NotBeEmpty()</c> and
    /// <c>StartWith("iverson-conformance-")</c>, which restated the constant's own prefix and could
    /// not fail for any value that kept it — including <c>iverson-conformance-nomic-embed-text</c>.
    /// It read as coverage while grading nothing.</para>
    /// </summary>
    [Fact]
    public void OverrideModelId_IsNotAModelThisBuildCouldResolve()
    {
        var family = EmbeddingPrefixes.Family(ModelRejectedScenario.OverrideModelId);

        family.Should().NotBe(EmbeddingPrefixes.Family(new EmbeddingServiceOptions().ModelId),
            "an override equal to the deployment's default model makes the guard's two models "
            + "compare equal, and nothing is rejected");
        EmbeddingPrefixes.Table.Keys.Should().NotContain(family,
            "every family in this table is one this build ships prefixes for, so it is a model a "
            + "deployment could plausibly be configured with");
    }
}
