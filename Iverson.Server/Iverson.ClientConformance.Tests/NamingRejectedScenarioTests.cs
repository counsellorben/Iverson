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
    /// <see cref="ThrowsFor"/> overrides per <c>TypeName</c> when a test needs the two fixtures
    /// judged differently; when unset, no request throws.
    /// </summary>
    private sealed class FakeMappingClient : ObjectMappingService.ObjectMappingServiceClient
    {
        public Func<string, Exception?>? ThrowsFor;

        public override AsyncUnaryCall<SchemaResponse> RegisterSchemaAsync(
            SchemaRequest request, Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            var ex = ThrowsFor is not null ? ThrowsFor(request.RootType.TypeName) : null;
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
        // Minor 3: round 1's replacement of the original java-only test dropped the assertion
        // pinning SKIP REASON TEXT, which is exactly what let SkipReason ignoring its `language`
        // parameter (always returning Java's registrar-specific text) regress unnoticed. "rust"
        // must get a reason about itself, not Java's separate-field-from-nav-property text.
        rustCell.Reason.Should().NotContain("separate field from");
        rustCell.Reason.Should().Contain("not a recognized conformance driver language");
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

    [Fact]
    public async Task SkipReason_Java_NamesTheRegistrarLimitation()
    {
        NamingRejectedScenario.SkipReason("java").Should().Contain("separate field from");
    }

    [Fact]
    public async Task SkipReason_UnrecognizedLanguage_DoesNotNameJavasLimitation()
    {
        // Minor 3: SkipReason("rust") once returned Java's registrar-specific explanation
        // regardless of the language asked about.
        var reason = NamingRejectedScenario.SkipReason("rust");

        reason.Should().Contain("rust");
        reason.Should().NotContain("separate field from");
        reason.Should().NotContain("SchemaRegistrar.inferForeignKey");
    }

    // ── Important (round 2): IVC-REG-003's server-side assertions must be attached — cited — on
    // EVERY driver-outcome path for the language carrying the server-side check, not merely the
    // Success-with-a-register-step path. Round 1 attached them only inside that one branch,
    // silently dropping the citation on the Success-with-no-register-step, Skipped, Broken and
    // unrecognized-language paths — the Skipped case is the dangerous one, since it lets a FULLY
    // GREEN run leave REG-003 completely unexercised.

    [Fact]
    public async Task RunAsync_ServerCheckLanguageDriverSkipped_ServerCheckPasses_CellIsSkip_ButCitesRegForeignKeyNaming()
    {
        // typescript's build step (npx tsc) is asked to run in a repo root that has no
        // Iverson.Clients/TypeScript directory at all, so ProcessStartInfo fails to start it
        // (ENOENT on the working directory) — DriverRunner reports this the same way it reports
        // an absent toolchain: Skipped. Deterministic, no live stack or fake driver needed. Only
        // "typescript" is requested, so ServerCheckLanguage's fallback lands the server-side check
        // on typescript itself (dotnet/java/go/python were not requested).
        var client = new FakeMappingClient
        {
            ThrowsFor = typeName => typeName == "S2NamingDotNetTags" ? ManyToManyRejection() : ManyToOneRejection(),
        };
        var scenario = new NamingRejectedScenario(new DriverRunner(repoRoot: "/tmp"), client);

        var cells = await scenario.RunAsync(["typescript"], Context(), actingToken: "acting-token");

        cells.Should().ContainSingle();
        var cell = cells[0];
        cell.Language.Should().Be("typescript");
        cell.Status.Should().Be(CellStatus.Skip);
        cell.Assertions.Should().Contain(a => a.RequirementId == Requirements.RegForeignKeyNamingEnforced);
        cell.Assertions.Should().OnlyContain(a => a.Passed);
    }

    [Fact]
    public async Task RunAsync_ServerCheckLanguageDriverSkipped_ServerCheckFails_CellIsFail_NotMaskedBySkip()
    {
        // The decisive mutation for the Important finding: if the fix regressed back to "only
        // attach serverCheckAssertions inside the Success branch", this test — where the server
        // itself accepted the misnamed fixtures (a real IVC-REG-003 regression) AND the driver
        // never ran (its toolchain directory is absent) — would still render as a fully green
        // Skip, exactly the failure mode the review raised.
        var client = new FakeMappingClient(); // no throws -> server wrongly ACCEPTS the misnaming
        var scenario = new NamingRejectedScenario(new DriverRunner(repoRoot: "/tmp"), client);

        var cells = await scenario.RunAsync(["typescript"], Context(), actingToken: "acting-token");

        cells.Should().ContainSingle();
        var cell = cells[0];
        cell.Language.Should().Be("typescript");
        cell.Status.Should().Be(CellStatus.Fail);
        cell.Assertions.Should().Contain(a => a.RequirementId == Requirements.RegForeignKeyNamingEnforced);
        cell.Assertions.Should().Contain(a => !a.Passed);
    }

    [Fact]
    public async Task RunAsync_ServerCheckLanguageDriverReportedNoRegisterStep_StillCitesRegForeignKeyNaming()
    {
        // python's driver.py is a real (fake, fixture-authored) script that always writes a phase
        // document with a step named something other than "register" — exercising the
        // Success-with-no-register-step branch (JudgeClientSideAssertions returns null) end to
        // end, exactly as a live driver's malformed/incomplete document would.
        using var fixture = FakeDriverFixture.WithSteps("python", ("not_register", true));
        var client = new FakeMappingClient
        {
            ThrowsFor = typeName => typeName == "S2NamingDotNetTags" ? ManyToManyRejection() : ManyToOneRejection(),
        };
        var scenario = new NamingRejectedScenario(new DriverRunner(repoRoot: fixture.RepoRoot), client);

        var cells = await scenario.RunAsync(["python"], Context(), actingToken: "acting-token");

        cells.Should().ContainSingle();
        var cell = cells[0];
        cell.Language.Should().Be("python");
        cell.Status.Should().Be(CellStatus.Fail);
        cell.Detail.Should().Contain("no 'register' step");
        cell.Assertions.Should().Contain(a => a.RequirementId == Requirements.RegForeignKeyNamingEnforced);
        cell.Assertions.Should().OnlyContain(a => a.Passed);
    }

    [Fact]
    public async Task RunAsync_ServerCheckLanguageDriverBroke_StillCitesRegForeignKeyNaming()
    {
        // python's driver.py fixture exits non-zero — the driver-broke branch — while the
        // server-side check itself passes; the cell must still be Fail (the driver's own breakage
        // is never masked) AND must still cite IVC-REG-003.
        using var fixture = FakeDriverFixture.ThatExits("python", exitCode: 1);
        var client = new FakeMappingClient
        {
            ThrowsFor = typeName => typeName == "S2NamingDotNetTags" ? ManyToManyRejection() : ManyToOneRejection(),
        };
        var scenario = new NamingRejectedScenario(new DriverRunner(repoRoot: fixture.RepoRoot), client);

        var cells = await scenario.RunAsync(["python"], Context(), actingToken: "acting-token");

        cells.Should().ContainSingle();
        var cell = cells[0];
        cell.Language.Should().Be("python");
        cell.Status.Should().Be(CellStatus.Fail);
        cell.Detail.Should().Contain("driver broke during the register phase");
        cell.Assertions.Should().Contain(a => a.RequirementId == Requirements.RegForeignKeyNamingEnforced);
        cell.Assertions.Should().OnlyContain(a => a.Passed);
    }

    [Fact]
    public async Task RunAsync_ServerCheckLanguageDriverBroke_ServerCheckFails_DetailNamesTheServerRegression()
    {
        // Minor 1 (round 3): pins MergeServerCheckIntoDriverFailure's docstring claim — "a real
        // server-side regression is never reported as merely 'the driver broke'" — the way its
        // sibling MergeServerCheckIntoDriverSkip is already pinned above. The server-side check
        // itself wrongly ACCEPTS the misnamed fixtures (a real IVC-REG-003 regression) AND the
        // driver also broke (python's fixture exits non-zero); the cell's Detail must name the
        // REG-003 server-side assertion, not the driver's own exit.
        using var fixture = FakeDriverFixture.ThatExits("python", exitCode: 1);
        var client = new FakeMappingClient(); // no throws -> server wrongly ACCEPTS the misnaming
        var scenario = new NamingRejectedScenario(new DriverRunner(repoRoot: fixture.RepoRoot), client);

        var cells = await scenario.RunAsync(["python"], Context(), actingToken: "acting-token");

        cells.Should().ContainSingle();
        var cell = cells[0];
        cell.Language.Should().Be("python");
        cell.Status.Should().Be(CellStatus.Fail);
        cell.Detail.Should().Contain("the server registered the descriptor");
        cell.Detail.Should().NotContain("driver broke during the register phase");
        cell.Assertions.Should().Contain(a => a.RequirementId == Requirements.RegForeignKeyNamingEnforced);
        cell.Assertions.Should().Contain(a => !a.Passed);
    }

    [Fact]
    public async Task RunAsync_ServerSideCheckThrowsForADriverLanguage_ProducesExactlyOneCell()
    {
        // Minor 1: when RunServerSideCheckAsync throws while serverCheckLanguage is one of
        // go/python/typescript, round 1 added a Fail cell for the harness-precondition failure
        // AND then still ran that same language's own driver phase, which added a SECOND cell for
        // the same (language, scenario) pair — Report.RenderText grids by FirstOrDefault, so the
        // second cell would silently never render. Only "typescript" is requested, with repoRoot
        // "/tmp" (no Iverson.Clients/TypeScript directory), so if the driver DID still run here it
        // would report Skipped and add that extra cell.
        var client = new FakeMappingClient { ThrowsFor = _ => new InvalidOperationException("boom") };
        var scenario = new NamingRejectedScenario(new DriverRunner(repoRoot: "/tmp"), client);

        var cells = await scenario.RunAsync(["typescript"], Context(), actingToken: "acting-token");

        cells.Should().ContainSingle();
        cells[0].Language.Should().Be("typescript");
        cells[0].Status.Should().Be(CellStatus.Fail);
        cells[0].Detail.Should().Contain("fixture registration failed");
    }

    [Fact]
    public void MergeServerCheckIntoDriverFailure_UnrecognizedLanguagePath_StillCitesRegForeignKeyNaming()
    {
        // The fourth path the review named — RunAsync's own "driverLanguages.Where(l =>
        // !reported.Contains(l))" guard — is, by construction, unreachable through the public
        // RunAsync API today: driverLanguages is pre-filtered to exactly {go, python,
        // typescript}, and DriverRunner's Drivers table always produces exactly one outcome (of
        // any of the three shapes) for each of those three languages, so `reported` always ends
        // up a superset of driverLanguages by the time that loop runs (mirrors
        // CrudRoundtripScenario's identical defensive guard, which IS reachable there only
        // because that scenario does not pre-filter). RunAsync wires that branch through the same
        // MergeServerCheckIntoDriverFailure helper the Broken-path test above already drives
        // end-to-end, so this test exercises that exact helper directly, standing in for the
        // unreachable branch and pinning that the citation survives on that code path too.
        var serverAssertions = NamingRejectedScenario.JudgeServerSide((RpcException)ManyToOneRejection())
            .Concat(NamingRejectedScenario.JudgeServerSideManyToMany((RpcException)ManyToManyRejection()))
            .ToList();

        var cell = NamingRejectedScenario.MergeServerCheckIntoDriverFailure(
            "go", "'go' is not a recognized conformance driver language", serverAssertions);

        cell.Status.Should().Be(CellStatus.Fail);
        cell.Detail.Should().Contain("not a recognized conformance driver language");
        cell.Assertions.Should().Contain(a => a.RequirementId == Requirements.RegForeignKeyNamingEnforced);
        cell.Assertions.Should().OnlyContain(a => a.Passed);
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

/// <summary>
/// A throwaway repo root containing exactly one real driver — python's, since it needs no build
/// step (<c>DriverRunner</c>'s python <c>DriverSpec</c> has a null <c>BuildCommand</c>) — a
/// standalone script this fixture authors, so the round-2 tests above can drive DriverRunner's
/// real subprocess machinery (build resolution, exec, --out parsing) for the Success-with-no-
/// register-step and Broken paths deterministically, without a live stack or the real Python
/// client. Every other DriverSpec (dotnet/go/typescript/java) is left entirely absent from this
/// tree on purpose: nothing in the naming-rejected scenario ever requests those languages in the
/// same test that also uses this fixture.
/// </summary>
internal sealed class FakeDriverFixture : IDisposable
{
    public string RepoRoot { get; }

    private FakeDriverFixture(string repoRoot) => RepoRoot = repoRoot;

    /// <summary>A python driver that always exits 0 and writes a phase document whose steps are
    /// exactly <paramref name="steps"/> — e.g. a document with no step named "register" at all,
    /// to drive <c>NamingRejectedScenario</c>'s Success-with-no-register-step branch.</summary>
    public static FakeDriverFixture WithSteps(string language, params (string Name, bool Ok)[] steps)
    {
        var stepsJson = string.Join(",", steps.Select(s =>
            $$"""{"name": "{{s.Name}}", "ok": {{(s.Ok ? "True" : "False")}}}"""));
        var script =
            $$"""
            import sys, json
            args = sys.argv[1:]
            out = args[args.index("--out") + 1]
            doc = {"language": "{{language}}", "phase": "register", "steps": [{{stepsJson}}]}
            with open(out, "w") as f:
                json.dump(doc, f)
            sys.exit(0)
            """;
        return Build(language, script);
    }

    /// <summary>A python driver that exits non-zero without writing an --out document at all —
    /// DriverRunner's driver-broke path.</summary>
    public static FakeDriverFixture ThatExits(string language, int exitCode)
    {
        var script =
            $"""
            import sys
            sys.stderr.write("fake driver deliberately broke for the naming-rejected round-2 tests")
            sys.exit({exitCode})
            """;
        return Build(language, script);
    }

    private static FakeDriverFixture Build(string language, string script)
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), $"iverson-naming-rejected-fixture-{Guid.NewGuid():N}");
        var driverDir = Path.Combine(repoRoot, "Iverson.Clients", "Python", "conformance");
        Directory.CreateDirectory(driverDir);
        File.WriteAllText(Path.Combine(driverDir, "driver.py"), script);
        return new FakeDriverFixture(repoRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(RepoRoot))
                Directory.Delete(RepoRoot, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup only — leaving a stray temp directory behind is not worth
            // failing an otherwise-passing test over.
        }
    }
}
