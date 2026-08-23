using FluentAssertions;
using Grpc.Core;
using Iverson.Client.Contracts;
using Iverson.ClientConformance.Scenarios;
using Xunit;

namespace Iverson.ClientConformance.Tests;

/// <summary>
/// Unit coverage for S10's judgement and fixture construction. Both halves are pure over what
/// <c>RegisterSchema</c> produced, so every branch is exercisable without a live stack.
///
/// <para>Every test names the mutation it exists to catch. The theme throughout: an arm that
/// asserts only "some rejection happened" grades nothing, because the registration path has half a
/// dozen guards that would reject these same fixtures for entirely the wrong reason.</para>
/// </summary>
public class TenantRejectedScenarioTests
{
    private static AsyncUnaryCall<T> CompletedCall<T>(T response) =>
        new(Task.FromResult(response), Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess, () => new Metadata(), () => { });

    private static AsyncUnaryCall<T> FaultedCall<T>(Exception ex) =>
        new(Task.FromException<T>(ex), Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess, () => new Metadata(), () => { });

    /// <summary>
    /// The message shape <c>SchemaRegistrationOrchestrator.RejectReservedTenantName</c> produces,
    /// reproduced here verbatim from its own interpolation. This is a CROSS-PROJECT COUPLING and
    /// deliberately so: <c>Iverson.ClientConformance.Tests</c> has no reference to
    /// <c>Iverson.Api</c>, so a fake that invented its own wording would let this file's assertions
    /// pass against text the server never emits. The server side of the same wording is pinned
    /// independently, per arm, by <c>Iverson.Api.Tests</c>'s reserved-name tests — if the two ever
    /// diverge, that suite is the one that says so.
    /// </summary>
    private static string ReservedNameMessage(string label, string typeName, string remedy) =>
        $"{label} '{TenantRejectedScenario.ReservedTenantColumnName}' on '{typeName}' uses "
        + $"'{TenantRejectedScenario.ReservedTenantColumnName}', which is a reserved server-owned "
        + $"column name. {remedy}; the server maintains the tenant column itself.";

    /// <summary>The message <c>RejectDeclaredTenantField</c> produces, same coupling, same reason.</summary>
    private static string DeclaredTenantFieldMessage(string typeName, string declared) =>
        $"tenant_field is no longer accepted, but '{typeName}' declares '{declared}'. The server owns "
        + "the tenant boundary and derives a row's tenant from the acting user's identity. Remove the "
        + "declaration from your client model.";

    /// <summary>
    /// A fake of the generated client that plays the server's ACTUAL guard order:
    /// <c>RejectDeclaredTenantField</c> first, then <c>RejectReservedTenantName</c>'s six arms in
    /// their source order. Playing the order rather than pattern-matching on the type name is what
    /// lets a test drop one arm and see the fixture fall through to a LATER arm — the exact
    /// failure the site-label assertion exists to catch, and one a name-keyed fake could not
    /// reproduce at all.
    /// </summary>
    private sealed class FakeMappingClient : ObjectMappingService.ObjectMappingServiceClient
    {
        /// <summary>Arms to skip, by site label — the mutation seam.</summary>
        public readonly HashSet<string> DisabledArms = new(StringComparer.Ordinal);

        public bool RejectDeclaredTenantField = true;

        public override AsyncUnaryCall<SchemaResponse> RegisterSchemaAsync(
            SchemaRequest request, Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            var type = request.RootType;

            if (RejectDeclaredTenantField && !string.IsNullOrEmpty(type.TenantField))
            {
                return FaultedCall<SchemaResponse>(new RpcException(new Status(StatusCode.InvalidArgument,
                    DeclaredTenantFieldMessage(type.TypeName, type.TenantField))));
            }

            foreach (var property in type.Properties)
            {
                if (IsReserved(property.Name))
                {
                    var label = property.IsKey ? "Key property" : "Property";
                    if (Fire(label, type.TypeName, "Rename the property") is { } key)
                        return key;

                    // FIDELITY, not decoration. With the property arm gone, the real server does
                    // NOT register the descriptor: ValidateIdentifier runs next and rejects the
                    // leading underscore — with InvalidArgument and a message that never mentions
                    // the reservation. So a site-blind "some rejection happened" assertion would
                    // stay GREEN through the loss of this guard, and only the message assertion
                    // catches it. A fake that simply registered here would hide that entirely.
                    return FaultedCall<SchemaResponse>(new RpcException(new Status(
                        StatusCode.InvalidArgument,
                        $"'{property.Name}' is not a valid property name on type '{type.TypeName}'.")));
                }
            }

            foreach (var relation in type.Relations)
            {
                if (IsReserved(relation.ForeignKey)
                    && Fire("Relation foreign key", type.TypeName, "Rename the property") is { } fk)
                    return fk;

                if (IsReserved(relation.PropertyName)
                    && Fire("Relation navigation property", type.TypeName, "Rename the navigation property") is { } nav)
                    return nav;
            }

            if (type.Authorization is { } authorization)
            {
                if (IsReserved(authorization.OwnerField)
                    && Fire("Owner field", type.TypeName, "Point owner_field at a property you declared") is { } owner)
                    return owner;

                foreach (var fieldPermission in authorization.FieldPermissions)
                {
                    if (IsReserved(fieldPermission.FieldName)
                        && Fire("Field permission", type.TypeName, "Point field_name at a property you declared") is { } fp)
                        return fp;
                }
            }

            return CompletedCall(new SchemaResponse { Success = true });
        }

        private AsyncUnaryCall<SchemaResponse>? Fire(string label, string typeName, string remedy) =>
            DisabledArms.Contains(label)
                ? null
                : FaultedCall<SchemaResponse>(new RpcException(new Status(
                    StatusCode.InvalidArgument, ReservedNameMessage(label, typeName, remedy))));

        private static bool IsReserved(string? name) =>
            string.Equals(name, TenantRejectedScenario.ReservedTenantColumnName, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ReportCell> RunCanonicalAsync(FakeMappingClient client) =>
        (await new TenantRejectedScenario(client).RunAsync(["dotnet", "go"], "acting-token"))
        .Single(c => c.Language == "dotnet");

    // ── IVC-REG-004 ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void JudgeDeclaredTenantField_RejectedWithTheServersOwnMessage_AllPass()
    {
        var caught = new RpcException(new Status(StatusCode.InvalidArgument,
            DeclaredTenantFieldMessage("S10TenantDeclared", "TenantId")));

        var assertions = TenantRejectedScenario.JudgeDeclaredTenantField(caught);

        assertions.Should().OnlyContain(a => a.Passed);
        assertions.Should().Contain(a => a.RequirementId == Requirements.RegDeclaredTenantFieldRejected);
    }

    /// <summary>THE mutation: the server goes back to silently ignoring a declared tenant field.</summary>
    [Fact]
    public void JudgeDeclaredTenantField_ServerRegisteredItAnyway_Reg004Fails()
    {
        var assertions = TenantRejectedScenario.JudgeDeclaredTenantField(null);

        assertions.Should().ContainSingle()
            .Which.RequirementId.Should().Be(Requirements.RegDeclaredTenantFieldRejected);
        assertions.Single().Passed.Should().BeFalse(
            "a silently ignored declaration leaves the caller believing it enforces a boundary the " +
            "server derives for itself — which is the whole reason this is an error and not a no-op");
    }

    [Fact]
    public void JudgeDeclaredTenantField_RejectedForSomeOtherReason_TheMessageAssertionsFail()
    {
        var caught = new RpcException(new Status(StatusCode.InvalidArgument,
            "'S10TenantDeclared' is not a valid identifier."));

        var assertions = TenantRejectedScenario.JudgeDeclaredTenantField(caught);

        assertions.Where(a => a.RequirementId == Requirements.ErrMessageNamesOffendingElement)
            .Should().HaveCount(2).And.OnlyContain(a => !a.Passed,
                "a rejection by a DIFFERENT guard must not be graded as this requirement passing");
    }

    [Fact]
    public void JudgeDeclaredTenantField_RejectedWithTheWrongStatusCode_TheErrAssertionFails()
    {
        var caught = new RpcException(new Status(StatusCode.PermissionDenied,
            DeclaredTenantFieldMessage("S10TenantDeclared", "TenantId")));

        TenantRejectedScenario.JudgeDeclaredTenantField(caught)
            .Single(a => a.RequirementId == Requirements.ErrRegistrationRejectionIsInvalidArgument)
            .Passed.Should().BeFalse();
    }

    // ── IVC-REG-005 ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_EveryArmIntact_TheCanonicalCellIsGreenAndCoversAllSixSites()
    {
        var cell = await RunCanonicalAsync(new FakeMappingClient());

        cell.Status.Should().Be(CellStatus.Ok, cell.Detail);
        cell.Assertions.Count(a => a.RequirementId == Requirements.RegReservedTenantColumnNameRejected)
            .Should().Be(TenantRejectedScenario.ReservedNameFixtures.Count,
                "one assertion per addressing site, so a site losing its guard reddens on its own");

        // THE WIRING mutation, the same one IdentityScenarioTests guards for S8: dropping
        // JudgeDeclaredTenantField from RunAsync's assertion list. Without this line that mutation
        // SURVIVES the whole suite — IVC-REG-004 silently stops reaching any cell while the const
        // stays cited (Check2 reads source text, not the call graph) and the matrix goes green.
        cell.Assertions.Should().Contain(a => a.RequirementId == Requirements.RegDeclaredTenantFieldRejected,
            "IVC-REG-004's judgement must actually reach the cell, not merely exist as a method");
    }

    /// <summary>
    /// THE mutation the per-site assertions exist for. Dropping the scalar-property arm leaves the
    /// fixture to fall through — and with a label-blind assertion it would fall through to nothing
    /// at all here, but the point generalises: a site-blind "some rejection happened" assertion is
    /// satisfied by whichever later guard the fixture also happens to trip.
    /// </summary>
    [Theory]
    [InlineData("Property", "scalar property")]
    [InlineData("Key property", "key property")]
    [InlineData("Relation foreign key", "relation foreign key")]
    [InlineData("Relation navigation property", "relation navigation property")]
    [InlineData("Owner field", "authorization.owner_field")]
    [InlineData("Field permission", "authorization.field_permissions[].field_name")]
    public async Task RunAsync_OneArmDropped_ExactlyThatSitesAssertionsRedden(string label, string site)
    {
        var client = new FakeMappingClient();
        client.DisabledArms.Add(label);

        var cell = await RunCanonicalAsync(client);

        cell.Status.Should().Be(CellStatus.Fail);
        cell.Assertions.Where(a => !a.Passed).Should().OnlyContain(a => a.Name.Contains(site, StringComparison.Ordinal),
            "the other five sites must stay green, or a single regression reads as six and the " +
            "matrix stops saying which guard was lost");
    }

    /// <summary>
    /// The site-label half, isolated: a rejection that names the right column but the WRONG SITE is
    /// a guard firing for another arm's reason, and must not be graded as this arm passing.
    /// </summary>
    [Fact]
    public void JudgeReservedNames_RejectedByADifferentSitesArm_TheSiteLabelAssertionFails()
    {
        var fixture = TenantRejectedScenario.ReservedNameFixtures.Single(f => f.Site == "scalar property");
        var caught = new RpcException(new Status(StatusCode.InvalidArgument,
            ReservedNameMessage("Owner field", fixture.TypeName, "Point owner_field at a property you declared")));

        var assertions = TenantRejectedScenario.JudgeReservedNames([(fixture, caught)]);

        assertions.Single(a => a.RequirementId == Requirements.ErrMessageNamesOffendingElement)
            .Passed.Should().BeFalse();
        assertions.Single(a => a.RequirementId == Requirements.RegReservedTenantColumnNameRejected)
            .Passed.Should().BeTrue("a rejection DID happen — it is the site that is wrong");
    }

    /// <summary>
    /// The other half: <c>ValidateIdentifier</c> rejects a leading underscore too, with a message
    /// that never mentions the reservation. A bare "InvalidArgument happened" assertion would grade
    /// that as this requirement passing, and the caller would get a message that never tells them
    /// the name is reserved.
    /// </summary>
    [Fact]
    public void JudgeReservedNames_RejectedByTheGenericIdentifierCheck_TheMessageAssertionFails()
    {
        var fixture = TenantRejectedScenario.ReservedNameFixtures.Single(f => f.Site == "scalar property");
        var caught = new RpcException(new Status(StatusCode.InvalidArgument,
            "'__TenantId' is not a valid property name on type 'S10TenantProperty'."));

        TenantRejectedScenario.JudgeReservedNames([(fixture, caught)])
            .Single(a => a.RequirementId == Requirements.ErrMessageNamesOffendingElement)
            .Passed.Should().BeFalse();
    }

    [Fact]
    public void JudgeReservedNames_ServerRegisteredTheDescriptor_Reg005FailsForThatSiteOnly()
    {
        var fixtures = TenantRejectedScenario.ReservedNameFixtures;
        var results = fixtures
            .Select(f => (f, f.Site == "authorization.owner_field"
                ? null
                : new RpcException(new Status(StatusCode.InvalidArgument,
                    ReservedNameMessage(f.SiteLabel, f.TypeName, "Rename the property")))))
            .ToList();

        var assertions = TenantRejectedScenario.JudgeReservedNames(results!);

        assertions.Where(a => !a.Passed).Should().ContainSingle()
            .Which.Name.Should().Contain("authorization.owner_field");
    }

    // ── the fixtures themselves ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Ruling-17 hazard, reproduced as a test: <c>RejectDeclaredTenantField</c> runs BEFORE
    /// <c>RejectReservedTenantName</c>. A reserved-name fixture that also populated
    /// <c>tenant_field</c> would be rejected by the earlier guard, and all six arms would report
    /// green while grading nothing at all — the exact trap Task 4 hit with the pre-existing
    /// rejection scenarios.
    /// </summary>
    [Fact]
    public async Task RunAsync_NoReservedNameFixtureDeclaresATenantField()
    {
        var client = new FakeMappingClient { RejectDeclaredTenantField = true };
        var cell = await RunCanonicalAsync(client);

        cell.Assertions
            .Where(a => a.RequirementId == Requirements.RegReservedTenantColumnNameRejected)
            .Should().OnlyContain(a => !a.Detail.Contains("tenant_field", StringComparison.Ordinal),
                "a fixture rejected by the tenant_field guard never reaches the arm it exists to grade");
        cell.Status.Should().Be(CellStatus.Ok, cell.Detail);
    }

    /// <summary>
    /// Every fixture in the list must be buildable. The builder throws on an unknown site rather
    /// than silently registering an empty descriptor, so adding a row to
    /// <c>ReservedNameFixtures</c> without a matching builder case fails loudly here instead of
    /// producing a seventh green assertion that grades nothing.
    /// </summary>
    [Fact]
    public async Task RunAsync_EveryDeclaredFixtureSiteIsActuallyBuildable()
    {
        var client = new FakeMappingClient();
        var cell = await RunCanonicalAsync(client);

        cell.Detail.Should().NotContain("fixture registration failed");
        foreach (var fixture in TenantRejectedScenario.ReservedNameFixtures)
            cell.Assertions.Should().Contain(a => a.Name.Contains(fixture.Site, StringComparison.Ordinal));
    }

    // ── the one-real-observation rendering ────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_OneLanguageCarriesTheOutcomeAndTheRestSkipNamingIt()
    {
        var cells = await new TenantRejectedScenario(new FakeMappingClient())
            .RunAsync(["go", "dotnet", "python"], "acting-token");

        cells.Should().HaveCount(3);
        cells.Count(c => c.Status != CellStatus.Skip).Should().Be(1,
            "no client library is involved, so the matrix must not read as three independent verifications");
        cells.Where(c => c.Status == CellStatus.Skip).Should().OnlyContain(c => c.Reason!.Contains("dotnet"));
    }

    [Fact]
    public void CanonicalLanguage_PrefersDotnetThenFallsBackDeterministically()
    {
        TenantRejectedScenario.CanonicalLanguage(["python", "dotnet"]).Should().Be("dotnet");
        TenantRejectedScenario.CanonicalLanguage(["python", "go"]).Should().Be("go");
        TenantRejectedScenario.CanonicalLanguage(["rust"]).Should().Be("rust",
            "a partial --languages run must never silently drop IVC-REG-004/005");
        TenantRejectedScenario.CanonicalLanguage([]).Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_NoLanguagesRequested_ReturnsNoCells() =>
        (await new TenantRejectedScenario(new FakeMappingClient()).RunAsync([], "acting-token"))
        .Should().BeEmpty();
}
