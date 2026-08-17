using FluentAssertions;
using Grpc.Core;
using Iverson.Client.Contracts;
using Iverson.ClientConformance.Scenarios;
using Xunit;

namespace Iverson.ClientConformance.Tests;

public class NamingRejectedScenarioTests
{
    private static AsyncUnaryCall<T> CompletedCall<T>(T response) =>
        new(Task.FromResult(response), Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess, () => new Metadata(), () => { });

    private static AsyncUnaryCall<T> FaultedCall<T>(Exception ex) =>
        new(Task.FromException<T>(ex), Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess, () => new Metadata(), () => { });

    /// <summary>
    /// Same hand-rolled fake seam as <c>NavPropertyRejectedScenarioTests</c> uses. The
    /// server-side check now posts TWO fixtures (many_to_one and many_to_many, IVC-REG-003), so
    /// this fake scripts per-request-shape rather than a single blanket exception —
    /// <see cref="RegisterSchemaThrows"/> stays as a simple "reject everything the same way"
    /// convenience for tests that don't care about the distinction; <see cref="ThrowsFor"/>
    /// overrides it per <c>TypeName</c> when a test needs the two fixtures judged differently.
    /// </summary>
    private sealed class FakeMappingClient : ObjectMappingService.ObjectMappingServiceClient
    {
        public Exception? RegisterSchemaThrows;
        public Func<string, Exception?>? ThrowsFor;

        public override AsyncUnaryCall<SchemaResponse> RegisterSchemaAsync(
            SchemaRequest request, Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            var ex = ThrowsFor is not null ? ThrowsFor(request.RootType.TypeName) : RegisterSchemaThrows;
            return ex is not null
                ? FaultedCall<SchemaResponse>(ex)
                : CompletedCall(new SchemaResponse { Success = true });
        }
    }

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
    public async Task RunAsync_JavaOnly_CarriesTheServerSideCheck_WithoutInvokingAnyDriver()
    {
        // Java declares a relation's FK as a separate field from the nav property AND its
        // registrar has no naming override at all, so there is no single wire name to misalign
        // client-side. But IVC-REG-003 is purely server-side, and ServerCheckPriority puts java
        // second (right after dotnet) precisely because it otherwise carries nothing but a bare
        // skip — so "--languages java" alone must NOT leave REG-003 untouched, per Minor 1 of the
        // Task 7 review. This must still never touch DriverRunner at all — repoRoot "/tmp" would
        // make any attempted build/exec fail loudly, which is exactly how a regression here would
        // be caught rather than silently building something.
        var client = new FakeMappingClient(); // no throws -> server "accepts" the misnamed fixtures
        var scenario = new NamingRejectedScenario(new DriverRunner(repoRoot: "/tmp"), client);

        var cells = await scenario.RunAsync(["java"], Context(), actingToken: "acting-token");

        cells.Should().ContainSingle();
        cells[0].Language.Should().Be("java");
        cells[0].Status.Should().Be(CellStatus.Fail);
    }

    [Fact]
    public async Task RunAsync_JavaAndAnUnrecognizedLanguageRequested_JavaCarriesTheServerCheck_RegardlessOfRequestOrder()
    {
        // Pins that java specifically wins ServerCheckPriority (not merely "whatever was listed
        // first") — the single-language fallback tests above cannot distinguish "java is priority
        // #2" from "java is simply the only/first requested language", since both produce the
        // same outcome when java is requested alone.
        var client = new FakeMappingClient
        {
            ThrowsFor = typeName => typeName == "S2NamingDotNetTags" ? ManyToManyRejection() : ManyToOneRejection(),
        };
        var scenario = new NamingRejectedScenario(new DriverRunner(repoRoot: "/tmp"), client);

        var cells = await scenario.RunAsync(["rust", "java"], Context(), actingToken: "acting-token");

        cells.Should().HaveCount(2);
        var javaCell = cells.Single(c => c.Language == "java");
        javaCell.Status.Should().Be(CellStatus.Ok);

        var rustCell = cells.Single(c => c.Language == "rust");
        rustCell.Status.Should().Be(CellStatus.Skip);
    }

    [Fact]
    public async Task RunAsync_UnrecognizedLanguageOnly_StillCarriesTheServerSideCheck()
    {
        // ServerCheckLanguage's final fallback (languages.FirstOrDefault()) is what guarantees
        // IVC-REG-003 is touched even when neither dotnet nor java was requested — proving the
        // fallback isn't reachable only via the java special case above.
        var client = new FakeMappingClient
        {
            ThrowsFor = typeName => typeName == "S2NamingDotNetTags" ? ManyToManyRejection() : ManyToOneRejection(),
        };
        var scenario = new NamingRejectedScenario(new DriverRunner(repoRoot: "/tmp"), client);

        var cells = await scenario.RunAsync(["rust"], Context(), actingToken: "acting-token");

        cells.Should().ContainSingle();
        cells[0].Language.Should().Be("rust");
        cells[0].Status.Should().Be(CellStatus.Ok);
    }

    private static Exception ManyToOneRejection() => new RpcException(new Status(
        StatusCode.InvalidArgument,
        "Relation 'Author' (ManyToOne) on 'S2NamingDotNet' declares foreign key " +
        "'WriterId', but a ManyToOne foreign key referencing 'S2NamingAuthor' must be " +
        "named 'S2NamingAuthorId'."));

    private static Exception ManyToManyRejection() => new RpcException(new Status(
        StatusCode.InvalidArgument,
        "Relation 'Tags' (ManyToMany) on 'S2NamingDotNetTags' declares foreign key " +
        "'TagRefs', but a ManyToMany foreign key referencing 'S2NamingTag' must be " +
        "named 'S2NamingTagIds'."));

    // ── dotnet: a real orchestrator-side (server) check, IVC-REG-003 — RunAsync end to end,
    // exercised through FakeMappingClient rather than a live gRPC channel. Posts TWO fixtures
    // (many_to_one, many_to_many); both must be rejected for the cell to go Ok.

    [Fact]
    public async Task RunAsync_Dotnet_ServerRejectsBothMisnamedFixtures_IsOk()
    {
        var client = new FakeMappingClient
        {
            ThrowsFor = typeName => typeName == "S2NamingDotNetTags" ? ManyToManyRejection() : ManyToOneRejection(),
        };
        var scenario = new NamingRejectedScenario(new DriverRunner(repoRoot: "/tmp"), client);

        var cells = await scenario.RunAsync(["dotnet"], Context(), actingToken: "acting-token");

        cells.Should().ContainSingle();
        cells[0].Language.Should().Be("dotnet");
        cells[0].Status.Should().Be(CellStatus.Ok);
    }

    [Fact]
    public async Task RunAsync_Dotnet_ServerAcceptsTheMisnamedForeignKey_IsFail()
    {
        var client = new FakeMappingClient();
        var scenario = new NamingRejectedScenario(new DriverRunner(repoRoot: "/tmp"), client);

        var cells = await scenario.RunAsync(["dotnet"], Context(), actingToken: "acting-token");

        cells.Should().ContainSingle();
        cells[0].Status.Should().Be(CellStatus.Fail);
    }

    [Fact]
    public async Task RunAsync_Dotnet_ServerRejectsManyToOneButAcceptsManyToMany_IsFail()
    {
        // The many_to_many fixture is what the review found missing entirely — this pins that a
        // server which regressed ONLY the many_to_many half of IVC-REG-003 still shows red,
        // rather than the many_to_one fixture's own success masking it.
        var client = new FakeMappingClient
        {
            ThrowsFor = typeName => typeName == "S2NamingDotNetTags" ? null : ManyToOneRejection(),
        };
        var scenario = new NamingRejectedScenario(new DriverRunner(repoRoot: "/tmp"), client);

        var cells = await scenario.RunAsync(["dotnet"], Context(), actingToken: "acting-token");

        cells.Should().ContainSingle();
        cells[0].Status.Should().Be(CellStatus.Fail);
        cells[0].Detail.Should().Contain("many_to_many");
    }

    [Fact]
    public void JudgeClientSideAssertions_DriverReportedRegistrationOk_Fails_TheMisnamedRelationShouldHaveBeenRejected()
    {
        var document = new PhaseDocument("go", "register", [new StepResult("register", true)]);

        var assertions = NamingRejectedScenario.JudgeClientSideAssertions(document);

        assertions.Should().NotBeNull();
        var cell = NamingRejectedScenario.BuildCell("go", assertions!);
        cell.Status.Should().Be(CellStatus.Fail);
        cell.Detail.Should().Contain("failed client-side, before any RPC");
    }

    [Fact]
    public void JudgeClientSideAssertions_ErrorNamesBothTheActualAndRequiredName_Passes()
    {
        var document = new PhaseDocument("python", "register",
        [
            new StepResult("register", false,
                Error: "PyBadArticle.writer_id declares a many_to_one relation to PyAuthor but " +
                       "is named 'WriterId' on the wire; a many_to_one foreign-key field must " +
                       "be named 'AuthorId' (rename the member to match)."),
        ]);

        var assertions = NamingRejectedScenario.JudgeClientSideAssertions(document);

        assertions.Should().NotBeNull();
        NamingRejectedScenario.BuildCell("python", assertions!).Status.Should().Be(CellStatus.Ok);
    }

    [Fact]
    public void JudgeClientSideAssertions_ErrorMissingTheRequiredForeignKeyName_Fails()
    {
        var document = new PhaseDocument("typescript", "register",
        [
            new StepResult("register", false, Error: "some unrelated registration error"),
        ]);

        var assertions = NamingRejectedScenario.JudgeClientSideAssertions(document);

        assertions.Should().NotBeNull();
        var cell = NamingRejectedScenario.BuildCell("typescript", assertions!);
        cell.Status.Should().Be(CellStatus.Fail);
        cell.Detail.Should().Contain("required foreign-key name");
    }

    // Guards the "actual, misnamed member" assertion independently of the "required foreign-key
    // name" assertion above: an error message can legitimately name AuthorId (e.g. because it is
    // quoting the schema it expected) while never naming the member the driver actually declared
    // ('writer'/'writerId'/'WriterId'). Without this test, disabling the actual-member-name check
    // entirely left the whole suite green — found during Task 11's mutation pass.
    [Fact]
    public void JudgeClientSideAssertions_ErrorMissingTheActualMemberName_Fails()
    {
        var document = new PhaseDocument("go", "register",
        [
            new StepResult("register", false,
                Error: "a many_to_one foreign-key field must be named 'AuthorId'"),
        ]);

        var assertions = NamingRejectedScenario.JudgeClientSideAssertions(document);

        assertions.Should().NotBeNull();
        var cell = NamingRejectedScenario.BuildCell("go", assertions!);
        cell.Status.Should().Be(CellStatus.Fail);
        cell.Detail.Should().Contain("actual, misnamed member");
    }

    [Fact]
    public void JudgeClientSideAssertions_NoRegisterStepReported_ReturnsNull()
    {
        var document = new PhaseDocument("go", "register", [new StepResult("register_author", true)]);

        var assertions = NamingRejectedScenario.JudgeClientSideAssertions(document);

        assertions.Should().BeNull();
    }

    // ── JudgeServerSide: IVC-REG-003, the server-side naming-rejection assertion (many_to_one arm).

    [Fact]
    public void JudgeServerSide_ServerRejectsWithInvalidArgumentNamingBothTerms_AllPass()
    {
        var caught = new RpcException(new Status(
            StatusCode.InvalidArgument,
            "Relation 'Author' (ManyToOne) on 'S2NamingDotNet' declares foreign key 'WriterId', " +
            "but a ManyToOne foreign key referencing 'S2NamingAuthor' must be named " +
            "'S2NamingAuthorId'."));

        var assertions = NamingRejectedScenario.JudgeServerSide(caught);

        assertions.Should().OnlyContain(a => a.Passed);
    }

    // ── JudgeServerSideManyToMany: IVC-REG-003's many_to_many arm — the fixture Important 1 of
    // the Task 7 review found had no citation behind it at all.

    [Fact]
    public void JudgeServerSideManyToMany_ServerRejectsWithInvalidArgumentNamingBothTerms_AllPass()
    {
        var caught = new RpcException(new Status(
            StatusCode.InvalidArgument,
            "Relation 'Tags' (ManyToMany) on 'S2NamingDotNetTags' declares foreign key 'TagRefs', " +
            "but a ManyToMany foreign key referencing 'S2NamingTag' must be named " +
            "'S2NamingTagIds'."));

        var assertions = NamingRejectedScenario.JudgeServerSideManyToMany(caught);

        assertions.Should().OnlyContain(a => a.Passed);
    }

    [Fact]
    public void JudgeServerSideManyToMany_RegistrationSucceeded_Fails()
    {
        var assertions = NamingRejectedScenario.JudgeServerSideManyToMany(caught: null);

        assertions.Should().ContainSingle();
        assertions[0].Passed.Should().BeFalse();
    }

    [Fact]
    public void JudgeServerSideManyToMany_MessageMissingTheRequiredForeignKeyName_FailsThatAssertionOnly()
    {
        var caught = new RpcException(new Status(StatusCode.InvalidArgument, "some unrelated error"));

        var assertions = NamingRejectedScenario.JudgeServerSideManyToMany(caught);

        assertions.Single(a => a.Name.Contains("required foreign-key name")).Passed.Should().BeFalse();
        assertions.Single(a => a.Name.Contains("actual, misnamed")).Passed.Should().BeFalse();
    }

    [Fact]
    public void JudgeServerSide_RegistrationSucceeded_Fails()
    {
        var assertions = NamingRejectedScenario.JudgeServerSide(caught: null);

        assertions.Should().ContainSingle();
        assertions[0].Passed.Should().BeFalse();
        assertions[0].Name.Should().Contain("rejects a misnamed foreign key");
    }

    [Fact]
    public void JudgeServerSide_WrongStatusCode_FailsTheStatusCodeAssertionOnly()
    {
        var caught = new RpcException(new Status(
            StatusCode.PermissionDenied,
            "Relation 'Author' (ManyToOne) on 'S2NamingDotNet' declares foreign key 'WriterId', " +
            "but a ManyToOne foreign key referencing 'S2NamingAuthor' must be named " +
            "'S2NamingAuthorId'."));

        var assertions = NamingRejectedScenario.JudgeServerSide(caught);

        assertions.Single(a => a.Name.Contains("rejects a misnamed foreign key")).Passed.Should().BeTrue();
        assertions.Single(a => a.Name.Contains("rejected with InvalidArgument")).Passed.Should().BeFalse();
    }

    [Fact]
    public void JudgeServerSide_MessageMissingTheRequiredForeignKeyName_FailsThatAssertionOnly()
    {
        var caught = new RpcException(new Status(StatusCode.InvalidArgument, "some unrelated error"));

        var assertions = NamingRejectedScenario.JudgeServerSide(caught);

        assertions.Single(a => a.Name.Contains("required foreign-key name")).Passed.Should().BeFalse();
        assertions.Single(a => a.Name.Contains("actual, misnamed")).Passed.Should().BeFalse();
    }

    [Fact]
    public void JudgeServerSide_MessageMissingTheActualMisnamedForeignKey_FailsThatAssertionOnly()
    {
        var caught = new RpcException(new Status(
            StatusCode.InvalidArgument,
            "a ManyToOne foreign key must be named 'S2NamingAuthorId'."));

        var assertions = NamingRejectedScenario.JudgeServerSide(caught);

        assertions.Single(a => a.Name.Contains("actual, misnamed")).Passed.Should().BeFalse();
        assertions.Single(a => a.Name.Contains("required foreign-key name")).Passed.Should().BeTrue();
    }
}
