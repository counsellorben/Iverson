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


    // ── RunAsync driven end to end (closes the LAST and worst of Ruling 38's residuals) ────────
    //
    // The `cells.Add(BuildDriverCell(...))` line in RunAsync was the least-defended of the three
    // ungraded call sites, and the reason is worth keeping: the other two carry requirement IDs,
    // so a live full-matrix run's UntouchedRequirementIds exit code reddens when their call site
    // is deleted — a CI-to-live delay, not a silent hole. These three client-side assertions carry
    // NO requirement ID, so that tally has nothing to miss. Delete the call and the driver-side
    // half of the naming check simply vanishes from the matrix with every cell still green, and
    // NOTHING anywhere would have noticed. This test is now the instrument.

    [Fact]
    public async Task RunAsync_DriverRejectedTheMisnamedRelation_TheClientSideJudgementReachesTheCell()
    {
        // python, alone: ServerCheckPriority picks it as the server-check carrier (dotnet/java/go
        // are not requested) AND it is a client-side-checked language, so the server assertions
        // merge into the very cell the driver phase builds — one cell, both halves.
        // The message names every identifier the server-side half of this scenario asserts on, so
        // the merged cell is green for the right reason. A bare "rejected" fails four of the
        // server's own IVC-ERR-002 assertions and would redden the cell for a reason that has
        // nothing to do with the call site under test.
        var client = new FakeMappingClient
        {
            ThrowsFor = _ => new RpcException(new Status(StatusCode.InvalidArgument,
                "WriterId must be named S2NamingAuthorId; TagRefs must be named S2NamingTagIds")),
        };

        var runner = new ScriptedDriverRunner().Script(Phase.Register,
            new DriverPhaseOutcome.Success("python",
                new PhaseDocument("python", "register",
                [
                    new StepResult("register", false,
                        Error: "relation 'writer' must declare foreign key AuthorId"),
                ])));

        var cells = await new NamingRejectedScenario(runner, client)
            .RunAsync(["python"], Context(), actingToken: "acting-token");

        var cell = cells.Should().ContainSingle().Subject;
        cell.Language.Should().Be("python");

        // All three client-side assertions, and all three passing — the driver rejected the
        // misnamed relation client-side and its message named both members. None of them can be
        // in this cell unless RunAsync actually called BuildDriverCell.
        var names = cell.Assertions.Select(a => a.Name).ToList();
        names.Should().Contain(n => n.Contains("failed client-side, before any RPC", StringComparison.Ordinal));
        names.Should().Contain(n => n.Contains("names the actual, misnamed member", StringComparison.Ordinal));
        names.Should().Contain(n => n.Contains("names the required foreign-key name", StringComparison.Ordinal));
        cell.Status.Should().Be(CellStatus.Ok);
    }

    [Fact]
    public async Task RunAsync_DriverAcceptedTheMisnamedRelation_TheClientSideFailureReachesTheCell()
    {
        // The negative direction: a driver whose library did NOT reject the misnamed relation must
        // redden the cell through the same call site. Without this, a judge that always emitted
        // passing assertions would satisfy the test above.
        var client = new FakeMappingClient
        {
            ThrowsFor = _ => new RpcException(new Status(StatusCode.InvalidArgument, "rejected")),
        };

        var runner = new ScriptedDriverRunner().Script(Phase.Register,
            new DriverPhaseOutcome.Success("python",
                new PhaseDocument("python", "register", [new StepResult("register", true)])));

        var cells = await new NamingRejectedScenario(runner, client)
            .RunAsync(["python"], Context(), actingToken: "acting-token");

        var cell = cells.Should().ContainSingle().Subject;
        cell.Status.Should().Be(CellStatus.Fail);
        cell.Assertions.Should().Contain(a =>
            a.Name.Contains("failed client-side, before any RPC", StringComparison.Ordinal) && !a.Passed);
    }

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

    /// <summary>
    /// Pins the ERR citations T12 added to the assertions this scenario already made. The status
    /// half discharges IVC-ERR-001 and the two message halves discharge IVC-ERR-002; those
    /// requirements are cited here rather than re-observed in the error-contract scenario, so a
    /// silently dropped citation would leave them exercised by nothing at runtime while the
    /// coverage gate still saw the identifier elsewhere in the source.
    /// </summary>
    [Fact]
    public void JudgeServerSide_CitesTheErrStatusAndMessageRequirements()
    {
        var assertions = NamingRejectedScenario.JudgeServerSide((RpcException)ManyToOneRejection());

        assertions.Should().ContainSingle(a =>
            a.Name.Contains("rejected with InvalidArgument", StringComparison.Ordinal)
            && a.RequirementId == Requirements.ErrRegistrationRejectionIsInvalidArgument);
        assertions.Count(a => a.RequirementId == Requirements.ErrMessageNamesOffendingElement).Should().Be(2);
    }

    [Fact]
    public void JudgeServerSideManyToMany_CitesTheErrStatusAndMessageRequirements()
    {
        var assertions = NamingRejectedScenario.JudgeServerSideManyToMany((RpcException)ManyToManyRejection());

        assertions.Should().ContainSingle(a =>
            a.Name.Contains("rejected with InvalidArgument", StringComparison.Ordinal)
            && a.RequirementId == Requirements.ErrRegistrationRejectionIsInvalidArgument);
        assertions.Count(a => a.RequirementId == Requirements.ErrMessageNamesOffendingElement).Should().Be(2);
    }

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
        var cell = ScenarioCells.Cell("go", NamingRejectedScenario.Name, assertions!);
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
        ScenarioCells.Cell("python", NamingRejectedScenario.Name, assertions!).Status.Should().Be(CellStatus.Ok);
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
        var cell = ScenarioCells.Cell("typescript", NamingRejectedScenario.Name, assertions!);
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
        var cell = ScenarioCells.Cell("go", NamingRejectedScenario.Name, assertions!);
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

    // ── Minor 2: the driver-side half of the naming check reaches a cell ──────────────────────

    /// <summary>
    /// <see cref="NamingRejectedScenario.BuildDriverCell"/> is the ONLY place the client-side
    /// judgement is filed into a cell. Replacing its <c>JudgeClientSideAssertions</c> call with
    /// <c>null</c> passed the entire suite and would pass the live untouched-requirement gate too:
    /// those three assertions carry NO requirement ID, so nothing that counts requirement IDs can
    /// see them go missing. The consequence is not a de-graded requirement — it is that the
    /// driver-side half of the naming check, which grades whether a client library rejects a
    /// misnamed relation BEFORE any RPC, silently vanishes from the matrix while every cell stays
    /// green. This test is what fails instead.
    ///
    /// <para>The mutant is distinguishable from the truth only because the count and the cell
    /// STATUS are both pinned: nulling the call yields a Fail cell with zero assertions, so
    /// asserting merely "a cell came back" would not see it.</para>
    /// </summary>
    [Fact]
    public void BuildDriverCell_TheClientSideJudgement_ReachesTheCell()
    {
        var document = new PhaseDocument("python", "register",
        [
            new StepResult("register", false,
                Error: "PyBadArticle.writer_id declares a many_to_one relation to PyAuthor but " +
                       "is named 'WriterId' on the wire; a many_to_one foreign-key field must " +
                       "be named 'AuthorId' (rename the member to match)."),
        ]);

        var expected = NamingRejectedScenario.JudgeClientSideAssertions(document);
        expected.Should().NotBeNull().And.HaveCount(3,
            "the fixture must actually produce the driver-side judgement this test claims to pin");

        var cell = NamingRejectedScenario.BuildDriverCell(
            "python", document, serverCheckAssertions: null, carriesServerCheck: false);

        cell.Status.Should().Be(CellStatus.Ok);
        cell.Assertions.Select(a => a.Name).Should().BeEquivalentTo(expected!.Select(a => a.Name),
            "every client-side assertion the judgement constructs must reach the cell");
    }

    /// <summary>
    /// The merge arm of the same seam: when this language is the one column carrying IVC-REG-003's
    /// server-side outcome, BOTH halves must reach the cell. Losing either half here would leave
    /// the other still present and the cell still rendered, which is what makes the count claim
    /// load-bearing rather than decorative.
    /// </summary>
    [Fact]
    public void BuildDriverCell_CarryingTheServerCheck_ReachesTheCellWithBothHalves()
    {
        var document = new PhaseDocument("dotnet", "register",
        [
            new StepResult("register", false,
                Error: "S2NamingArticle.WriterId declares a many_to_one relation to " +
                       "S2NamingAuthor but a many_to_one foreign-key field must be named " +
                       "'S2NamingAuthorId'."),
        ]);

        var serverSide = NamingRejectedScenario.JudgeServerSide(
            new RpcException(new Status(StatusCode.InvalidArgument,
                "S2NamingArticle.WriterId: a many_to_one foreign key must be named 'S2NamingAuthorId'.")));

        var cell = NamingRejectedScenario.BuildDriverCell(
            "dotnet", document, serverSide, carriesServerCheck: true);

        cell.Assertions.Should().HaveCount(3 + serverSide.Count);
        cell.Assertions.Select(a => a.RequirementId).Should().Contain(
            Requirements.RegForeignKeyNamingEnforced,
            "the server-side half must still be merged in");
    }

    [Fact]
    public void BuildDriverCell_DocumentWithNoRegisterStep_FailsTheCellNamingTheMissingStep()
    {
        var cell = NamingRejectedScenario.BuildDriverCell(
            "go", new PhaseDocument("go", "register", []),
            serverCheckAssertions: null, carriesServerCheck: false);

        cell.Status.Should().Be(CellStatus.Fail);
        cell.Detail.Should().Contain("the driver reported no 'register' step");
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
