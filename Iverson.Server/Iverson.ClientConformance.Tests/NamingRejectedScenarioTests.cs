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

    /// <summary>Same hand-rolled fake seam as <c>NavPropertyRejectedScenarioTests</c> uses.</summary>
    private sealed class FakeMappingClient : ObjectMappingService.ObjectMappingServiceClient
    {
        public Exception? RegisterSchemaThrows;

        public override AsyncUnaryCall<SchemaResponse> RegisterSchemaAsync(
            SchemaRequest request, Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            RegisterSchemaThrows is not null
                ? FaultedCall<SchemaResponse>(RegisterSchemaThrows)
                : CompletedCall(new SchemaResponse { Success = true });
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
    public async Task RunAsync_Java_RendersAsSkip_WithoutInvokingAnyDriver()
    {
        // Java declares a relation's FK as a separate field from the nav property AND its
        // registrar has no naming override at all, so there is no single wire name to misalign
        // and no server-side check to exercise either. This must never touch DriverRunner at
        // all — repoRoot "/tmp" would make any attempted build/exec fail loudly, which is exactly
        // how a regression here would be caught rather than silently building something.
        var scenario = new NamingRejectedScenario(new DriverRunner(repoRoot: "/tmp"), new FakeMappingClient());

        var cells = await scenario.RunAsync(["java"], Context(), actingToken: "acting-token");

        cells.Should().ContainSingle();
        cells[0].Language.Should().Be("java");
        cells[0].Status.Should().Be(CellStatus.Skip);
        cells[0].Reason.Should().Contain("separate field");
    }

    [Fact]
    public async Task RunAsync_NoDriverCheckedLanguagesRequested_ReturnsOnlySkipCells()
    {
        var scenario = new NamingRejectedScenario(new DriverRunner(repoRoot: "/tmp"), new FakeMappingClient());

        var cells = await scenario.RunAsync(["java"], Context(), actingToken: "acting-token");

        cells.Should().ContainSingle();
        cells[0].Language.Should().Be("java");
        cells[0].Status.Should().Be(CellStatus.Skip);
    }

    // ── dotnet: now a real orchestrator-side (server) check, IVC-REG-001 — RunAsync end to end,
    // exercised through FakeMappingClient rather than a live gRPC channel.

    [Fact]
    public async Task RunAsync_Dotnet_ServerRejectsTheMisnamedForeignKey_IsOk()
    {
        var client = new FakeMappingClient
        {
            RegisterSchemaThrows = new RpcException(new Status(
                StatusCode.InvalidArgument,
                "Relation 'Author' (ManyToOne) on 'S2NamingDotNet' declares foreign key " +
                "'WriterId', but a ManyToOne foreign key referencing 'S2NamingAuthor' must be " +
                "named 'S2NamingAuthorId'.")),
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

    // ── JudgeServerSide: IVC-REG-001, the server-side naming-rejection assertion.

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
