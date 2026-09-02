using System.Text.Json;
using FluentAssertions;
using Iverson.Client.Contracts;
using Iverson.ClientConformance.Scenarios;
using Xunit;

namespace Iverson.ClientConformance.Tests;

/// <summary>
/// Unit coverage for S12 <c>model-inherited</c> (<c>IVC-DECL-007</c>). <see cref="InheritedModelScenario.JudgeInheritance"/>
/// is pure over an already-parsed <see cref="TypeDescriptor"/> and graded directly; <c>RunAsync</c>
/// is driven END TO END through <see cref="ScriptedDriverRunner"/>, which is what makes reading the
/// descriptor through <see cref="Verifier.ParseDescriptor"/> — rather than the raw
/// <see cref="JsonElement"/> — observable at all. That distinction is the whole point of this
/// scenario: see <see cref="RunAsync_EmbeddingModelIdOmittedEntirely_GoTypeScriptWireShape_FailsTheAssertion"/>.
/// </summary>
public class InheritedModelScenarioTests
{
    private const string Expected = InheritedModelScenario.ExpectedModelId;

    private static DriverContext Context() => new(
        Scenario: InheritedModelScenario.Name,
        Type: string.Empty,
        Tenant: "iverson-loadtest-dynamic",
        GrpcUrl: "http://localhost:5000",
        ClientId: "client-id",
        ClientSecret: "client-secret",
        TokenEndpoint: "http://localhost:9000/application/o/token/",
        ActingToken: "acting-token",
        OwnerId: "owner-id",
        IdPrefix: "s12-");

    private static Assertion Named(IEnumerable<Assertion> assertions, string fragment) =>
        assertions.Single(a => a.Name.Contains(fragment, StringComparison.Ordinal));

    // ── the pure judgement, over an already-parsed descriptor ────────────────────────────────

    private static PropertyDescriptor Key() => new() { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true };

    private static PropertyDescriptor Embedding(string? modelId) => new()
    {
        Name = "Title", ClrType = ClrType.ClrString, IsEmbedding = true, ModelId = modelId ?? string.Empty,
    };

    private static PropertyDescriptor Chunk(string? chunkModelId) => new()
    {
        Name = "Body", ClrType = ClrType.ClrString, IsChunk = true, ChunkModelId = chunkModelId ?? string.Empty,
    };

    private static TypeDescriptor Descriptor(params PropertyDescriptor[] properties)
    {
        var descriptor = new TypeDescriptor { TypeName = "S12InheritedDotnet" };
        descriptor.Properties.AddRange(properties);
        return descriptor;
    }

    [Fact]
    public void JudgeInheritance_S11ShapedDescriptorCarryingTheDeclaredModel_PassesEveryArm()
    {
        var assertions = InheritedModelScenario.JudgeInheritance(
            "dotnet", Descriptor(Key(), Embedding(Expected), Chunk(Expected)));

        assertions.Should().OnlyContain(a => a.Passed);
        assertions.Should().HaveCount(4, "at-least-one-embedding, at-least-one-chunk, and one arm per property");
        assertions.Should().OnlyContain(a => a.RequirementId == Requirements.DeclEmbeddingModelInherited);
    }

    [Fact]
    public void JudgeInheritance_EmbeddingPropertyCarriesAnEmptyModelId_FailsOnlyThatArm()
    {
        var assertions = InheritedModelScenario.JudgeInheritance(
            "dotnet", Descriptor(Key(), Embedding(string.Empty), Chunk(Expected)));

        Named(assertions, "embedding property 'Title'").Passed.Should().BeFalse();
        Named(assertions, "chunk property 'Body'").Passed.Should().BeTrue();
    }

    /// <summary>
    /// Pins the equality constraint itself, not just the empty/omitted-vs-present distinction: a
    /// WRONG but non-empty model id must fail too. Without this case, <c>== ExpectedModelId</c>
    /// could regress to <c>!= string.Empty</c> and every other test in this file would stay green.
    /// </summary>
    [Fact]
    public void JudgeInheritance_EmbeddingPropertyCarriesAWrongNonEmptyModelId_FailsOnlyThatArm()
    {
        var assertions = InheritedModelScenario.JudgeInheritance(
            "dotnet", Descriptor(Key(), Embedding("all-minilm"), Chunk(Expected)));

        Named(assertions, "embedding property 'Title'").Passed.Should().BeFalse();
        Named(assertions, "chunk property 'Body'").Passed.Should().BeTrue();
    }

    [Fact]
    public void JudgeInheritance_ChunkPropertyCarriesAnEmptyChunkModelId_FailsOnlyThatArm()
    {
        var assertions = InheritedModelScenario.JudgeInheritance(
            "dotnet", Descriptor(Key(), Embedding(Expected), Chunk(string.Empty)));

        Named(assertions, "chunk property 'Body'").Passed.Should().BeFalse();
        Named(assertions, "embedding property 'Title'").Passed.Should().BeTrue();
    }

    /// <summary>
    /// A chunk-only property must never be judged on <c>model_id</c> — it is never stamped for a
    /// chunk-only property, so a blanket check would fail every correctly inheriting client. The
    /// absence of an "embedding property 'Body'" arm here IS the assertion.
    /// </summary>
    [Fact]
    public void JudgeInheritance_ChunkOnlyProperty_IsNeverJudgedOnModelId()
    {
        var assertions = InheritedModelScenario.JudgeInheritance("dotnet", Descriptor(Key(), Chunk(Expected)));

        // "embedding property '" (with the opening quote) is the per-property arm's own wording;
        // the at-least-one-embedding arm above reads "...at least one embedding property" with no
        // trailing quote, so this excludes it deliberately rather than by accident.
        assertions.Should().NotContain(a => a.Name.Contains("embedding property '", StringComparison.Ordinal));
    }

    [Fact]
    public void JudgeInheritance_NoEmbeddingProperty_FailsTheAtLeastOneEmbeddingArm()
    {
        var assertions = InheritedModelScenario.JudgeInheritance("dotnet", Descriptor(Key(), Chunk(Expected)));

        Named(assertions, "at least one embedding property").Passed.Should().BeFalse();
        Named(assertions, "at least one chunk property").Passed.Should().BeTrue();
    }

    [Fact]
    public void JudgeInheritance_NoChunkProperty_FailsTheAtLeastOneChunkArm()
    {
        var assertions = InheritedModelScenario.JudgeInheritance("dotnet", Descriptor(Key(), Embedding(Expected)));

        Named(assertions, "at least one chunk property").Passed.Should().BeFalse();
        Named(assertions, "at least one embedding property").Passed.Should().BeTrue();
    }

    [Fact]
    public void JudgeInheritance_NeitherKindDeclared_FailsBothAtLeastOneArmsVacuously()
    {
        var assertions = InheritedModelScenario.JudgeInheritance("dotnet", Descriptor(Key()));

        assertions.Should().HaveCount(2, "no per-property arm can fire when neither kind is declared");
        assertions.Should().OnlyContain(a => !a.Passed);
    }

    [Fact]
    public void JudgeInheritance_EveryArm_CitesDecl007()
    {
        var assertions = InheritedModelScenario.JudgeInheritance(
            "dotnet", Descriptor(Key(), Embedding(Expected), Chunk(Expected)));

        assertions.Should().OnlyContain(a => a.RequirementId == Requirements.DeclEmbeddingModelInherited);
    }

    // ── the driver contract T7 implements against ─────────────────────────────────────────────

    [Theory]
    [InlineData("dotnet", "S12InheritedDotnet")]
    [InlineData("python", "S12InheritedPython")]
    [InlineData("typescript", "S12InheritedTypescript")]
    [InlineData("go", "S12InheritedGo")]
    [InlineData("java", "S12InheritedJava")]
    public void TypeNameFor_IsAPerLanguageLegalIdentifier(string language, string expected)
    {
        InheritedModelScenario.TypeNameFor(language).Should().Be(expected);
        InheritedModelScenario.TypeNameFor(language).Should().MatchRegex("^[A-Za-z][A-Za-z0-9]*$");
    }

    // ── raw-JSON descriptors: the exact shapes the five drivers actually put on the wire ──────

    /// <summary>
    /// Builds the register step's descriptor as hand-written JSON text rather than through
    /// protobuf's <c>JsonFormatter</c>. That is deliberate: <c>JsonFormatter.Default</c> omits a
    /// proto3 default (zero) value regardless of which shape it is asked to represent, so it
    /// cannot produce the "field present but empty" shape .NET/Java/Python send — only hand-written
    /// JSON can put the two wire shapes (present-and-empty vs. omitted-entirely) under independent
    /// control, which is exactly what the omission-vs-empty distinction this scenario exists to
    /// grade requires.
    /// </summary>
    /// <param name="modelIdField">The literal JSON fragment for the embedding property's
    /// <c>modelId</c> (e.g. <c>"\"modelId\":\"nomic-embed-text\""</c> or <c>"\"modelId\":\"\""</c>),
    /// or <c>null</c> to omit the field entirely — the Go/TypeScript shape.</param>
    /// <param name="chunkModelIdField">Same, for the chunk property's <c>chunkModelId</c>.</param>
    private static JsonElement DescriptorJson(
        string typeName,
        string? modelIdField,
        string? chunkModelIdField,
        bool includeEmbeddingProperty = true,
        bool includeChunkProperty = true)
    {
        var key = "{\"name\":\"Id\",\"clrType\":\"CLR_GUID\",\"isKey\":true}";

        var properties = new List<string> { key };

        if (includeEmbeddingProperty)
        {
            properties.Add("{\"name\":\"Title\",\"clrType\":\"CLR_STRING\",\"isEmbedding\":true"
                + (modelIdField is null ? "" : "," + modelIdField) + "}");
        }

        if (includeChunkProperty)
        {
            properties.Add("{\"name\":\"Body\",\"clrType\":\"CLR_STRING\",\"isChunk\":true"
                + (chunkModelIdField is null ? "" : "," + chunkModelIdField) + "}");
        }

        var json = "{\"typeName\":\"" + typeName + "\",\"properties\":[" + string.Join(",", properties) + "]}";
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static string ModelIdField(string value) => $"\"modelId\":\"{value}\"";
    private static string ChunkModelIdField(string value) => $"\"chunkModelId\":\"{value}\"";

    private static DriverPhaseOutcome.Success Registered(string language, JsonElement descriptor) =>
        new(language, new PhaseDocument(language, "register",
        [
            new StepResult(InheritedModelScenario.RegisterStepName, true, TypeDescriptor: descriptor),
        ]));

    // ── RunAsync, driven end to end ───────────────────────────────────────────────────────────

    /// <summary>
    /// (a) A driver reporting a correct descriptor — every field present, carrying the declared
    /// model — passes. The baseline every other test below deviates from one field at a time.
    /// </summary>
    [Fact]
    public async Task RunAsync_ADriverReportingACorrectDescriptor_Passes()
    {
        var typeName = InheritedModelScenario.TypeNameFor("dotnet");
        var descriptor = DescriptorJson(typeName, ModelIdField(Expected), ChunkModelIdField(Expected));
        var runner = new ScriptedDriverRunner().Script(Phase.Register, Registered("dotnet", descriptor));

        var cell = (await new InheritedModelScenario(runner).RunAsync(["dotnet"], Context(), "acting-token"))
            .Should().ContainSingle().Subject;

        cell.Status.Should().Be(CellStatus.Ok);
        cell.Scenario.Should().Be(InheritedModelScenario.Name);
    }

    /// <summary>
    /// Pins the equality constraint at the wiring level (mirrors
    /// <see cref="JudgeInheritance_EmbeddingPropertyCarriesAWrongNonEmptyModelId_FailsOnlyThatArm"/>
    /// one layer up, through <c>RunAsync</c>): a driver reporting a WRONG but non-empty model id
    /// must fail. This is the case that catches <c>== ExpectedModelId</c> regressing to
    /// <c>!= string.Empty</c> — every empty-string and omitted-field test in this file would stay
    /// green under that regression, since neither of them ever supplies a wrong NON-empty value.
    /// </summary>
    [Fact]
    public async Task RunAsync_EmbeddingModelIdReportedAsAWrongNonEmptyModel_FailsTheAssertion()
    {
        var typeName = InheritedModelScenario.TypeNameFor("dotnet");
        var descriptor = DescriptorJson(typeName, ModelIdField("all-minilm"), ChunkModelIdField(Expected));
        var runner = new ScriptedDriverRunner().Script(Phase.Register, Registered("dotnet", descriptor));

        var cell = (await new InheritedModelScenario(runner).RunAsync(["dotnet"], Context(), "acting-token"))
            .Should().ContainSingle().Subject;

        cell.Status.Should().Be(CellStatus.Fail);
        var arm = Named(cell.Assertions, "embedding property 'Title'");
        arm.Passed.Should().BeFalse();
        arm.Detail.Should().Contain("all-minilm");
    }

    /// <summary>
    /// Pins the OTHER load-bearing constraint at the wiring level: the descriptor is read through
    /// <c>Verifier.ParseDescriptor</c>, protobuf's own JSON parser, which — per the proto3 JSON
    /// spec — accepts EITHER the lowerCamelCase name or the original proto field name
    /// (<c>object_mapping.proto</c> declares <c>type_name</c>, <c>clr_type</c>, <c>is_embedding</c>,
    /// <c>model_id</c>, <c>is_chunk</c>, <c>chunk_model_id</c>). Every other test in this file
    /// scripts camelCase JSON, which a hand-rolled <c>JsonElement</c> indexer keyed on camelCase
    /// property names would also read correctly — so none of them can tell
    /// <c>Verifier.ParseDescriptor</c> apart from such an indexer. This one can: a camelCase
    /// indexer reads every flag on this descriptor as unset (missing `isEmbedding`/`isChunk`) and
    /// fails the at-least-one-of-each-kind arms, while the real parser resolves the proto
    /// field-name JSON to the same descriptor as the camelCase form and passes.
    /// </summary>
    [Fact]
    public async Task RunAsync_DescriptorInProtoFieldNameForm_IsStillParsedCorrectly()
    {
        var typeName = InheritedModelScenario.TypeNameFor("dotnet");
        var json = "{\"type_name\":\"" + typeName + "\",\"properties\":["
            + "{\"name\":\"Id\",\"clr_type\":\"CLR_GUID\",\"is_key\":true},"
            + "{\"name\":\"Title\",\"clr_type\":\"CLR_STRING\",\"is_embedding\":true,\"model_id\":\"" + Expected + "\"},"
            + "{\"name\":\"Body\",\"clr_type\":\"CLR_STRING\",\"is_chunk\":true,\"chunk_model_id\":\"" + Expected + "\"}"
            + "]}";
        var descriptor = JsonDocument.Parse(json).RootElement.Clone();
        var runner = new ScriptedDriverRunner().Script(Phase.Register, Registered("dotnet", descriptor));

        var cell = (await new InheritedModelScenario(runner).RunAsync(["dotnet"], Context(), "acting-token"))
            .Should().ContainSingle().Subject;

        cell.Status.Should().Be(CellStatus.Ok);
    }

    /// <summary>
    /// The fixture-type contract check: a driver that reports the CORRECT model id under this
    /// scenario's step, but on some OTHER already-registered type (here <c>S11ModelDotnet</c>,
    /// which carries the same <see cref="InheritedModelScenario.ExpectedModelId"/> via its own
    /// direct <c>[IversonEmbeddingModel]</c> declaration rather than inheritance), must fail —
    /// otherwise a driver could satisfy this scenario by replaying an S11 fixture without the
    /// inheritance path ever running.
    /// </summary>
    [Fact]
    public async Task RunAsync_TheDriverReportedADifferentType_FailsTheFixtureTypeAssertion()
    {
        var descriptor = DescriptorJson("S11ModelDotnet", ModelIdField(Expected), ChunkModelIdField(Expected));
        var runner = new ScriptedDriverRunner().Script(Phase.Register, Registered("dotnet", descriptor));

        var cell = (await new InheritedModelScenario(runner).RunAsync(["dotnet"], Context(), "acting-token"))
            .Should().ContainSingle().Subject;

        cell.Status.Should().Be(CellStatus.Fail);
        var arm = Named(cell.Assertions, "registered this scenario's fixture type");
        arm.Passed.Should().BeFalse();
        arm.Detail.Should().Contain("S11ModelDotnet");
    }

    /// <summary>
    /// (b) A driver reporting <c>modelId: ""</c> ON THE EMBEDDING PROPERTY — the .NET/Java/Python
    /// wire shape for an undeclared model — fails the assertion. Distinct from (c): here the field
    /// is present, just empty.
    /// </summary>
    [Fact]
    public async Task RunAsync_EmbeddingModelIdReportedAsAnEmptyString_FailsTheAssertion()
    {
        var typeName = InheritedModelScenario.TypeNameFor("dotnet");
        var descriptor = DescriptorJson(typeName, ModelIdField(string.Empty), ChunkModelIdField(Expected));
        var runner = new ScriptedDriverRunner().Script(Phase.Register, Registered("dotnet", descriptor));

        var cell = (await new InheritedModelScenario(runner).RunAsync(["dotnet"], Context(), "acting-token"))
            .Should().ContainSingle().Subject;

        cell.Status.Should().Be(CellStatus.Fail);
        Named(cell.Assertions, "embedding property 'Title'").Passed.Should().BeFalse();
    }

    /// <summary>
    /// (c) THE regression test. A driver whose descriptor OMITS the <c>modelId</c> field entirely
    /// (the Go/TypeScript wire shape) must fail the assertion exactly as (b) does. The two
    /// constraints in the scenario's class doc exist for this case alone: read through
    /// <c>Verifier.ParseDescriptor</c> (protobuf's parser lands an omitted field on the same
    /// default as an explicit <c>""</c>), and assert EQUALITY with the expected id rather than
    /// inequality with <c>""</c> (against raw JSON, a missing field reads <c>null</c>, and
    /// <c>null != ""</c> would pass). Either constraint dropped turns this test green on the exact
    /// defect it exists to catch.
    /// </summary>
    [Fact]
    public async Task RunAsync_EmbeddingModelIdOmittedEntirely_GoTypeScriptWireShape_FailsTheAssertion()
    {
        var typeName = InheritedModelScenario.TypeNameFor("go");
        var descriptor = DescriptorJson(typeName, modelIdField: null, ChunkModelIdField(Expected));
        var runner = new ScriptedDriverRunner().Script(Phase.Register, Registered("go", descriptor));

        var cell = (await new InheritedModelScenario(runner).RunAsync(["go"], Context(), "acting-token"))
            .Should().ContainSingle().Subject;

        cell.Status.Should().Be(CellStatus.Fail);
        var arm = Named(cell.Assertions, "embedding property 'Title'");
        arm.Passed.Should().BeFalse();
        arm.Detail.Should().Contain("modelId=''");
    }

    /// <summary>The chunk-side mirror of (c): an omitted <c>chunkModelId</c> must fail too.</summary>
    [Fact]
    public async Task RunAsync_ChunkModelIdOmittedEntirely_TypeScriptWireShape_FailsTheAssertion()
    {
        var typeName = InheritedModelScenario.TypeNameFor("typescript");
        var descriptor = DescriptorJson(typeName, ModelIdField(Expected), chunkModelIdField: null);
        var runner = new ScriptedDriverRunner().Script(Phase.Register, Registered("typescript", descriptor));

        var cell = (await new InheritedModelScenario(runner).RunAsync(["typescript"], Context(), "acting-token"))
            .Should().ContainSingle().Subject;

        cell.Status.Should().Be(CellStatus.Fail);
        Named(cell.Assertions, "chunk property 'Body'").Passed.Should().BeFalse();
    }

    /// <summary>
    /// (d) A descriptor with no chunk property fails the at-least-one-of-each-kind requirement,
    /// even though the one property it does declare inherits correctly.
    /// </summary>
    [Fact]
    public async Task RunAsync_DescriptorHasNoChunkProperty_FailsTheAtLeastOneChunkRequirement()
    {
        var typeName = InheritedModelScenario.TypeNameFor("dotnet");
        var descriptor = DescriptorJson(
            typeName, ModelIdField(Expected), chunkModelIdField: null, includeChunkProperty: false);
        var runner = new ScriptedDriverRunner().Script(Phase.Register, Registered("dotnet", descriptor));

        var cell = (await new InheritedModelScenario(runner).RunAsync(["dotnet"], Context(), "acting-token"))
            .Should().ContainSingle().Subject;

        cell.Status.Should().Be(CellStatus.Fail);
        Named(cell.Assertions, "at least one chunk property").Passed.Should().BeFalse();
    }

    /// <summary>The embedding-side mirror of (d).</summary>
    [Fact]
    public async Task RunAsync_DescriptorHasNoEmbeddingProperty_FailsTheAtLeastOneEmbeddingRequirement()
    {
        var typeName = InheritedModelScenario.TypeNameFor("dotnet");
        var descriptor = DescriptorJson(
            typeName, modelIdField: null, ChunkModelIdField(Expected), includeEmbeddingProperty: false);
        var runner = new ScriptedDriverRunner().Script(Phase.Register, Registered("dotnet", descriptor));

        var cell = (await new InheritedModelScenario(runner).RunAsync(["dotnet"], Context(), "acting-token"))
            .Should().ContainSingle().Subject;

        cell.Status.Should().Be(CellStatus.Fail);
        Named(cell.Assertions, "at least one embedding property").Passed.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_TheDriverToolchainIsAbsent_RendersASkip()
    {
        var runner = new ScriptedDriverRunner()
            .Script(Phase.Register, new DriverPhaseOutcome.Skipped("go", "skip (go not found)"));

        var cell = (await new InheritedModelScenario(runner).RunAsync(["go"], Context(), "acting-token"))
            .Should().ContainSingle().Subject;

        cell.Status.Should().Be(CellStatus.Skip);
        cell.Reason.Should().Be("skip (go not found)");
    }

    [Fact]
    public async Task RunAsync_TheDriverBroke_FailsThatColumnNamingTheExitCode()
    {
        var runner = new ScriptedDriverRunner()
            .Script(Phase.Register, new DriverPhaseOutcome.Broken("java", 2, "unsupported scenario 'model-inherited'"));

        var cell = (await new InheritedModelScenario(runner).RunAsync(["java"], Context(), "acting-token"))
            .Should().ContainSingle().Subject;

        cell.Status.Should().Be(CellStatus.Fail);
        cell.Detail.Should().Contain("exit 2").And.Contain("unsupported scenario");
    }

    [Fact]
    public async Task RunAsync_TheDriverReportedNoRegisterStep_FailsThatColumn()
    {
        var runner = new ScriptedDriverRunner().Script(Phase.Register,
            new DriverPhaseOutcome.Success("python",
                new PhaseDocument("python", "register", [new StepResult("something_else", true)])));

        var cell = (await new InheritedModelScenario(runner).RunAsync(["python"], Context(), "acting-token"))
            .Should().ContainSingle().Subject;

        cell.Status.Should().Be(CellStatus.Fail);
        Named(cell.Assertions, "reported the descriptor it registered").Detail.Should()
            .Contain(InheritedModelScenario.RegisterStepName);
    }

    [Fact]
    public async Task RunAsync_ALanguageTheRunnerDoesNotRecognize_FailsThatColumn()
    {
        var runner = new ScriptedDriverRunner().Script(Phase.Register,
            Registered("dotnet", DescriptorJson(
                InheritedModelScenario.TypeNameFor("dotnet"), ModelIdField(Expected), ChunkModelIdField(Expected))));

        var cells = await new InheritedModelScenario(runner).RunAsync(["dotnet", "rust"], Context(), "acting-token");

        var rust = cells.Single(c => c.Language == "rust");
        rust.Status.Should().Be(CellStatus.Fail);
        rust.Detail.Should().Contain("not a recognized conformance driver language");
    }

    [Fact]
    public async Task RunAsync_NoLanguagesRequested_RunsNothingAndReturnsNoCells()
    {
        var runner = new ScriptedDriverRunner();

        var cells = await new InheritedModelScenario(runner).RunAsync([], Context(), "acting-token");

        cells.Should().BeEmpty();
        runner.Calls.Should().BeEmpty();
    }
}
