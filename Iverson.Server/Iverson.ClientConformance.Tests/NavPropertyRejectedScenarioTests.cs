using FluentAssertions;
using Grpc.Core;
using Iverson.Client.Contracts;
using Iverson.ClientConformance.Scenarios;
using Xunit;

namespace Iverson.ClientConformance.Tests;

public class NavPropertyRejectedScenarioTests
{
    private static DriverContext Context() => new(
        Scenario: NavPropertyRejectedScenario.Name,
        Type: string.Empty,
        Tenant: "iverson-loadtest-dynamic",
        GrpcUrl: "http://localhost:5000",
        ClientId: "client-id",
        ClientSecret: "client-secret",
        TokenEndpoint: "http://localhost:9000/application/o/token/",
        ActingToken: "acting-token",
        OwnerId: "owner-id",
        IdPrefix: "s3-");

    private static AsyncUnaryCall<T> CompletedCall<T>(T response) =>
        new(Task.FromResult(response), Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess, () => new Metadata(), () => { });

    private static AsyncUnaryCall<T> FaultedCall<T>(Exception ex) =>
        new(Task.FromException<T>(ex), Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess, () => new Metadata(), () => { });

    /// <summary>
    /// A hand-rolled fake of the generated <c>ObjectMappingServiceClient</c> — its async members
    /// are all <c>virtual</c> (grpc-dotnet codegen) and it has a protected parameterless
    /// constructor, exactly the seam <c>NavPropertyRejectedScenario</c>'s constructor takes.
    /// Registration and the illegal post are each independently scriptable so a test can drive
    /// either failure path without a live channel.
    /// </summary>
    private sealed class FakeMappingClient : ObjectMappingService.ObjectMappingServiceClient
    {
        public Exception? RegisterSchemaThrows;
        public Exception? PostThrows;

        // Scripts the SEPARATE, self-contained "S3NavCollide" registration attempt
        // (IVC-REG-002). Defaults to a realistic rejection so tests that only care about the
        // write-payload check (RegisterSchemaThrows/PostThrows) don't have to also script this —
        // the collision assertion just passes quietly for them.
        public Exception? RegisterCollisionThrows = new RpcException(new Status(
            StatusCode.InvalidArgument,
            "Relation 'S3NavCollideAuthorId' (ManyToOne) on 'S3NavCollide' has a navigation-" +
            "property name identical to its foreign key 'S3NavCollideAuthorId'."));

        public override AsyncUnaryCall<SchemaResponse> RegisterSchemaAsync(
            SchemaRequest request, Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            if (request.RootType.TypeName == "S3NavCollide")
                return RegisterCollisionThrows is not null
                    ? FaultedCall<SchemaResponse>(RegisterCollisionThrows)
                    : CompletedCall(new SchemaResponse { Success = true });

            return RegisterSchemaThrows is not null
                ? FaultedCall<SchemaResponse>(RegisterSchemaThrows)
                : CompletedCall(new SchemaResponse { Success = true });
        }

        public override AsyncUnaryCall<MappingResponse> PostAsync(
            MappingWriteRequest request, Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            PostThrows is not null
                ? FaultedCall<MappingResponse>(PostThrows)
                : CompletedCall(new MappingResponse { Success = true });
    }


    [Fact]
    public void Judge_ServerRejectsWithInvalidArgumentNamingBothTerms_AllPass()
    {
        var caught = new RpcException(new Status(
            StatusCode.InvalidArgument,
            "Relation 'Author' is a navigation property and cannot be written — send " +
            "'S3NavAuthorId' instead."));

        var assertions = NavPropertyRejectedScenario.Judge(caught);

        assertions.Should().OnlyContain(a => a.Passed);
    }

    [Fact]
    public void Judge_WriteSucceeded_Fails_TheServerShouldHaveRejectedIt()
    {
        var assertions = NavPropertyRejectedScenario.Judge(caught: null);

        assertions.Should().ContainSingle();
        assertions[0].Passed.Should().BeFalse();
        assertions[0].Name.Should().Contain("rejects a navigation-property key");
    }

    [Fact]
    public void Judge_WrongStatusCode_FailsTheStatusCodeAssertion_ButStillNamesBothTerms()
    {
        // A regression that turns this into, say, PermissionDenied (the authorization gate
        // firing instead of relation validation) must be visible as ITSELF — a status-code
        // failure — not folded into the message-text checks that might coincidentally still pass
        // or fail for unrelated reasons.
        var caught = new RpcException(new Status(
            StatusCode.PermissionDenied,
            "Relation 'Author' is a navigation property and cannot be written — send " +
            "'S3NavAuthorId' instead."));

        var assertions = NavPropertyRejectedScenario.Judge(caught);

        var statusAssertion = assertions.Single(a => a.Name.Contains("rejected with InvalidArgument"));
        statusAssertion.Passed.Should().BeFalse();

        var navAssertion = assertions.Single(a => a.Name.Contains("navigation property"));
        navAssertion.Passed.Should().BeTrue();
    }

    [Fact]
    public void Judge_MessageMissingTheForeignKeyName_FailsThatAssertionOnly()
    {
        var caught = new RpcException(new Status(
            StatusCode.InvalidArgument,
            "Relation 'Author' is a navigation property and cannot be written."));

        var assertions = NavPropertyRejectedScenario.Judge(caught);

        var fkAssertion = assertions.Single(a => a.Name.Contains("required foreign key"));
        fkAssertion.Passed.Should().BeFalse();

        var navAssertion = assertions.Single(a => a.Name.Contains("navigation property"));
        navAssertion.Passed.Should().BeTrue();
    }

    // Guards the nav-property-name assertion independently of the message assertion above.
    // Note 'AuthorId' (the required foreign key) always contains 'Author' (the navigation
    // property) as a substring in this fixture, so a message naming the foreign key necessarily
    // also "names" the navigation property — there is no message that passes the foreign-key
    // check while failing this one. The only message shape that can isolate a broken
    // nav-property-name check is one missing BOTH terms. Without this test, disabling the
    // nav-property-name check entirely (hardcoding it to true) left the whole suite green —
    // found during Task 11's mutation pass, the same gap S2's actual-member-name assertion had.
    [Fact]
    public void Judge_MessageMissingBothTerms_FailsTheNavPropertyNameAssertion()
    {
        var caught = new RpcException(new Status(
            StatusCode.InvalidArgument,
            "some unrelated validation error"));

        var assertions = NavPropertyRejectedScenario.Judge(caught);

        var navAssertion = assertions.Single(a => a.Name.Contains("navigation property"));
        navAssertion.Passed.Should().BeFalse();

        var fkAssertion = assertions.Single(a => a.Name.Contains("required foreign key"));
        fkAssertion.Passed.Should().BeFalse();
    }

    // Round 2 fix: the nav-property assertion above previously used a bare case-insensitive
    // Contains("Author"), and "S3NavAuthorId" (the required foreign key) contains "Author" as a
    // substring — so a message naming ONLY the foreign key, never the navigation property at all,
    // used to satisfy BOTH assertions and the two stopped being independent observations. This
    // fixture is exactly that shape: it names 'S3NavAuthorId' but never quotes 'Author' the way
    // RelationValidator.cs actually emits it ("Relation '{PropertyName}' is a navigation property
    // ..."). The foreign-key assertion must still pass; the nav-property assertion must now fail,
    // proving the two are falsifiable independently of one another.
    [Fact]
    public void Judge_MessageNamesOnlyTheForeignKey_FailsTheNavPropertyAssertionButNotTheForeignKeyAssertion()
    {
        var caught = new RpcException(new Status(
            StatusCode.InvalidArgument,
            "Schema is invalid: the required foreign key S3NavAuthorId was not supplied."));

        var assertions = NavPropertyRejectedScenario.Judge(caught);

        var navAssertion = assertions.Single(a => a.Name.Contains("navigation property"));
        navAssertion.Passed.Should().BeFalse();

        var fkAssertion = assertions.Single(a => a.Name.Contains("required foreign key"));
        fkAssertion.Passed.Should().BeTrue();
    }

    // ── CanonicalLanguage: the fix for the "five independent-looking ok cells for one
    // orchestrator-side check" finding. Only this one language's cell may ever carry the real
    // Ok/Fail outcome; every other requested language must render as Skip instead — see
    // RunAsync's use of CanonicalLanguage, exercised end to end by the live run recorded in the
    // report (Fix round 1), since RunAsync itself needs a live gRPC channel to invoke.

    [Fact]
    public void CanonicalLanguage_PicksDotnetFirst_RegardlessOfRequestOrder()
    {
        // The fixed priority list, not input order, decides the column — so a rerun with the
        // same requested set always lands the result in the same place even if --languages was
        // typed in a different order.
        NavPropertyRejectedScenario.CanonicalLanguage(["java", "python", "dotnet", "go"])
            .Should().Be("dotnet");
    }

    [Fact]
    public void CanonicalLanguage_WithoutDotnetRequested_FallsToTheNextPriorityLanguage()
    {
        NavPropertyRejectedScenario.CanonicalLanguage(["typescript", "java", "python"])
            .Should().Be("java");
    }

    [Fact]
    public void CanonicalLanguage_IsCaseInsensitive()
    {
        // The match is case-insensitive, but the returned value is the priority list's own
        // canonical casing ("dotnet"), not whatever casing the caller happened to request.
        NavPropertyRejectedScenario.CanonicalLanguage(["TypeScript", "DOTNET"])
            .Should().Be("dotnet");
    }

    [Fact]
    public void CanonicalLanguage_SingleLanguageRequested_ReturnsIt()
    {
        NavPropertyRejectedScenario.CanonicalLanguage(["go"]).Should().Be("go");
    }

    [Fact]
    public void CanonicalLanguage_EmptyCollection_ReturnsEmptyString_DoesNotThrow()
    {
        NavPropertyRejectedScenario.CanonicalLanguage([]).Should().Be(string.Empty);
    }

    // ── RunAsync: this is the actual tripwire for the "one server-side result broadcast as five
    // independent-looking ok cells" regression. A test asserting only that the canonical column
    // is ok would still pass if RunAsync were reverted to
    // `languages.Select(l => ReportCell.Ok(l, Name))` — every assertion here also pins that the
    // OTHER requested languages are Skip, not Ok, which is the one thing that form could not
    // produce.

    [Fact]
    public async Task RunAsync_ServerRejectsTheIllegalPayload_OnlyTheCanonicalColumnIsOk_RestAreSkip()
    {
        var client = new FakeMappingClient
        {
            PostThrows = new RpcException(new Status(
                StatusCode.InvalidArgument,
                "Relation 'Author' is a navigation property and cannot be written — send " +
                "'S3NavAuthorId' instead.")),
        };
        var scenario = new NavPropertyRejectedScenario(client);

        var cells = await scenario.RunAsync(
            ["python", "java", "dotnet", "go", "typescript"], Context(), actingToken: "acting-token");

        cells.Should().HaveCount(5);

        // "dotnet" wins CanonicalLanguage's fixed priority order regardless of request order.
        var canonical = cells.Single(c => c.Language == "dotnet");
        canonical.Status.Should().Be(CellStatus.Ok);

        var others = cells.Where(c => c.Language != "dotnet").ToList();
        others.Should().HaveCount(4);
        // The exact regression this test exists to catch: a broadcast implementation would mark
        // every one of these Ok too.
        others.Should().OnlyContain(c => c.Status == CellStatus.Skip);
        others.Should().OnlyContain(c => c.Reason != null && c.Reason.Contains("dotnet"));
    }

    [Fact]
    public async Task RunAsync_ServerAcceptsTheIllegalPayload_CanonicalColumnFails_RestStillSkip()
    {
        // The write "succeeding" (no RpcException at all) is Judge's own first failure mode —
        // confirms a real assertion failure also lands only in the canonical column, not five
        // separate Fail cells.
        var client = new FakeMappingClient();
        var scenario = new NavPropertyRejectedScenario(client);

        var cells = await scenario.RunAsync(["go", "python"], Context(), actingToken: "acting-token");

        cells.Should().HaveCount(2);
        var canonical = cells.Single(c => c.Language == "go");
        canonical.Status.Should().Be(CellStatus.Fail);
        canonical.Detail.Should().Contain("rejects a navigation-property key");

        var skipped = cells.Single(c => c.Language == "python");
        skipped.Status.Should().Be(CellStatus.Skip);
    }

    [Fact]
    public async Task RunAsync_FixtureRegistrationFails_LandsAsASingleFailInTheCanonicalColumn_NotBroadcast()
    {
        var client = new FakeMappingClient
        {
            // Any non-RpcException also has to be caught: RunAsync's registration try/catch
            // catches `Exception`, not just `RpcException` — a transport-level failure or a bad
            // descriptor both have to land the same way.
            RegisterSchemaThrows = new InvalidOperationException("channel is not connected"),
        };
        var scenario = new NavPropertyRejectedScenario(client);

        var cells = await scenario.RunAsync(["java", "dotnet"], Context(), actingToken: "acting-token");

        cells.Should().HaveCount(2);
        var canonical = cells.Single(c => c.Language == "dotnet");
        canonical.Status.Should().Be(CellStatus.Fail);
        canonical.Detail.Should().Contain("fixture registration failed");
        canonical.Detail.Should().Contain("channel is not connected");

        var skipped = cells.Single(c => c.Language == "java");
        skipped.Status.Should().Be(CellStatus.Skip);
    }

    [Fact]
    public async Task RunAsync_NoLanguagesRequested_ReturnsNoCells()
    {
        var scenario = new NavPropertyRejectedScenario(new FakeMappingClient());

        var cells = await scenario.RunAsync([], Context(), actingToken: "acting-token");

        cells.Should().BeEmpty();
    }

    // ── JudgeCollision: IVC-REG-002, the registration-time PropertyName/ForeignKey collision
    // rejection — a distinct observation from Judge's write-payload check above.

    [Fact]
    public void JudgeCollision_ServerRejectsWithInvalidArgument_AllPass()
    {
        var caught = new RpcException(new Status(
            StatusCode.InvalidArgument,
            "Relation 'S3NavCollideAuthorId' has a navigation-property name identical to its " +
            "foreign key."));

        var assertions = NavPropertyRejectedScenario.JudgeCollision(caught);

        assertions.Should().OnlyContain(a => a.Passed);
    }

    [Fact]
    public void JudgeCollision_RegistrationSucceeded_Fails_TheCollisionShouldHaveBeenRejected()
    {
        var assertions = NavPropertyRejectedScenario.JudgeCollision(caught: null);

        assertions.Should().ContainSingle();
        assertions[0].Passed.Should().BeFalse();
        assertions[0].Name.Should().Contain("PropertyName/ForeignKey collision");
    }

    [Fact]
    public void JudgeCollision_WrongStatusCode_FailsTheStatusCodeAssertionOnly()
    {
        var caught = new RpcException(new Status(StatusCode.PermissionDenied, "denied"));

        var assertions = NavPropertyRejectedScenario.JudgeCollision(caught);

        assertions.Single(a => a.Name.Contains("PropertyName/ForeignKey collision")).Passed.Should().BeTrue();
        assertions.Single(a => a.Name.Contains("rejected with InvalidArgument")).Passed.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_ServerAcceptsTheCollidingFixture_CanonicalColumnFails_OnCollisionAssertionSpecifically()
    {
        // The write-payload rejection still succeeds; only the SEPARATE registration-time
        // collision check silently fails to reject — this must surface as a real Fail, not be
        // masked by the payload assertions that are otherwise all green.
        var client = new FakeMappingClient
        {
            RegisterCollisionThrows = null,
            PostThrows = new RpcException(new Status(
                StatusCode.InvalidArgument,
                "Relation 'Author' is a navigation property and cannot be written — send " +
                "'S3NavAuthorId' instead.")),
        };
        var scenario = new NavPropertyRejectedScenario(client);

        var cells = await scenario.RunAsync(["dotnet"], Context(), actingToken: "acting-token");

        var canonical = cells.Single(c => c.Language == "dotnet");
        canonical.Status.Should().Be(CellStatus.Fail);
        canonical.Detail.Should().Contain("PropertyName/ForeignKey collision");
    }
}
