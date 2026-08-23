using System.Text.Json;
using FluentAssertions;
using Iverson.ClientConformance.Scenarios;
using Xunit;

namespace Iverson.ClientConformance.Tests;

/// <summary>
/// Unit coverage for S8's judgement, which is pure over reported data
/// (<see cref="IdentityScenario.JudgeWrite"/>, <see cref="IdentityScenario.Judge"/>,
/// <see cref="IdentityScenario.SeededKey"/>, <see cref="IdentityScenario.ReadString"/>,
/// <see cref="IdentityScenario.ReadStatusCode"/>) and so is exercisable without a live stack.
///
/// Every test here names the mutation it would catch: an assertion that cannot be made to fail is
/// not evidence, and the whole point of this file is that each IDN requirement's cell goes red for
/// exactly the defect its statement describes and for nothing else.
/// </summary>
public class IdentityScenarioTests
{
    private const string Tenant = "tenant_bypass";
    private const string Owner = "d3adbeef-0000-0000-0000-000000000001";
    private static readonly Guid Key = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OtherKey = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static PhaseDocument ReadDocument(params StepResult[] steps) => new("dotnet", "read", steps);

    private static StepResult ReadStep(Guid key, string tenant, string owner) =>
        new(IdentityScenario.ReadStepName, true,
            Entity: JsonSerializer.SerializeToElement(new { key = key.ToString(), tenant, owner }));

    private static StepResult DeniedStep(int? statusCode) =>
        new(IdentityScenario.DeniedStepName, true,
            Entity: JsonSerializer.SerializeToElement(new { statusCode, status = statusCode?.ToString() ?? "succeeded" }));

    /// <summary>
    /// What the orchestrator sees when the server behaved: Postgres holds the acting tenant in the
    /// SERVER-owned column and the driver's wrong value still sitting in the driver's OWN column,
    /// and the gRPC read of the same row carries neither the server-owned column nor anything like
    /// it.
    /// </summary>
    private static IdentityScenario.TenantObservation Derived(
        string? storedTenant = Tenant,
        string? storedUserColumn = IdentityScenario.WrongTenantValue,
        IReadOnlyCollection<string>? grpcFields = null)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (storedTenant is not null) row[PostgresProbe.ServerOwnedTenantColumn] = storedTenant;
        if (storedUserColumn is not null) row[IdentityScenario.DriverDeclaredTenantColumn] = storedUserColumn;
        row["Id"] = Key.ToString();

        return new IdentityScenario.TenantObservation(
            Attempted: true, ProbeFailure: null, PostgresRow: row,
            GrpcFieldNames: grpcFields ?? ["Id", IdentityScenario.DriverDeclaredTenantColumn, "OwnerId", "Label"]);
    }

    private static IReadOnlyList<Assertion> JudgeHappy(
        StepResult? read = null,
        StepResult? denied = null,
        Guid? seeded = null,
        IdentityScenario.TenantObservation? observation = null) =>
        IdentityScenario.Judge("dotnet", Tenant, Owner, seeded ?? Key,
            ReadDocument(read ?? ReadStep(Key, Tenant, Owner), denied ?? DeniedStep(IdentityScenario.DeniedStatusCode)),
            observation ?? Derived());

    private static IReadOnlyList<Assertion> Cited(IReadOnlyList<Assertion> assertions, string requirementId) =>
        assertions.Where(a => a.RequirementId == requirementId).ToList();

    private static Assertion Named(IReadOnlyList<Assertion> assertions, string fragment) =>
        assertions.Single(a => a.Name.Contains(fragment, StringComparison.Ordinal));

    // ── the happy path ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Judge_ReadBackAndDenialBothAsExpected_AllAssertionsPass()
    {
        JudgeHappy().Should().OnlyContain(a => a.Passed);
    }

    [Fact]
    public void JudgeWrite_WriteStepSucceeded_AssertionPassesAndCitesIdn001()
    {
        var assertions = IdentityScenario.JudgeWrite(
            "dotnet", new PhaseDocument("dotnet", "write", [new StepResult(IdentityScenario.WriteStepName, true)]));

        Cited(assertions, "IVC-IDN-001").Should().ContainSingle().Which.Passed.Should().BeTrue();
    }

    // ── IVC-IDN-001: both identities carried, write accepted ──────────────────────────────────

    [Fact]
    public void JudgeWrite_WriteStepFailed_Idn001Fails()
    {
        var assertions = IdentityScenario.JudgeWrite(
            "dotnet", new PhaseDocument("dotnet", "write",
                [new StepResult(IdentityScenario.WriteStepName, false, Error: "PermissionDenied")]));

        Cited(assertions, "IVC-IDN-001").Should().ContainSingle().Which.Passed.Should().BeFalse();
    }

    [Fact]
    public void JudgeWrite_NoWriteStepAtAll_Idn001FailsRatherThanBeingSkipped()
    {
        var assertions = IdentityScenario.JudgeWrite(
            "dotnet", new PhaseDocument("dotnet", "write", [new StepResult("something_else", true)]));

        Cited(assertions, "IVC-IDN-001").Should().ContainSingle().Which.Passed.Should().BeFalse();
    }

    // ── the backstop ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Judge_NoSeededKey_BackstopFailsAndCarriesNoRequirementId()
    {
        var assertions = IdentityScenario.Judge(
            "dotnet", Tenant, Owner, seededKey: null,
            ReadDocument(ReadStep(Key, Tenant, Owner), DeniedStep(IdentityScenario.DeniedStatusCode)),
            IdentityScenario.TenantObservation.NotAttempted);

        var backstop = Named(assertions, "reported a row key");
        backstop.Passed.Should().BeFalse();
        backstop.RequirementId.Should().BeNull(
            "no IVC-IDN-* statement owns 'this language seeded a row' — it is a property of the harness's fixture");
    }

    [Fact]
    public void Judge_SeededKeyPresent_BackstopPasses()
    {
        Named(JudgeHappy(), "reported a row key").Passed.Should().BeTrue();
    }

    // ── IVC-IDN-002: the acting user's row is readable back, carrying its owner ───────────────

    [Fact]
    public void Judge_ReadStepFailed_Idn002ReadabilityFails()
    {
        var assertions = JudgeHappy(read: new StepResult(IdentityScenario.ReadStepName, false, Error: "not found"));

        Named(assertions, "readable back").Passed.Should().BeFalse();
        Named(assertions, "readable back").RequirementId.Should().Be("IVC-IDN-002");
    }

    [Fact]
    public void Judge_NoReadStepAtAll_Idn002FailsRatherThanBeingSkipped()
    {
        var assertions = IdentityScenario.Judge("dotnet", Tenant, Owner, Key,
            ReadDocument(DeniedStep(IdentityScenario.DeniedStatusCode)), Derived());

        Cited(assertions, "IVC-IDN-002").Should().OnlyContain(a => !a.Passed);
    }

    [Fact]
    public void Judge_ReadReturnedADifferentRow_Idn002ReadabilityFails()
    {
        Named(JudgeHappy(read: ReadStep(OtherKey, Tenant, Owner)), "readable back")
            .Passed.Should().BeFalse("a driver that read some other row has not read back the row it wrote");
    }

    [Fact]
    public void Judge_OwnerIsNotTheActingUsersSubject_Idn002OwnerFails()
    {
        var assertions = JudgeHappy(read: ReadStep(Key, Tenant, "somebody-else"));

        Named(assertions, "owner identity").Passed.Should().BeFalse();
        Named(assertions, "owner identity").RequirementId.Should().Be("IVC-IDN-002");
    }

    [Fact]
    public void Judge_OwnerAbsentFromTheReportedEntity_Idn002OwnerFails()
    {
        var read = new StepResult(IdentityScenario.ReadStepName, true,
            Entity: JsonSerializer.SerializeToElement(new { key = Key.ToString(), tenant = Tenant }));

        Named(JudgeHappy(read: read), "owner identity").Passed.Should().BeFalse();
    }

    // ── IVC-IDN-003: tenancy derived from the acting user, and enforced ───────────────────────

    /// <summary>
    /// RE-POINTED, not deleted. This test used to drive the driver-side read-back — the assertion
    /// that became unfalsifiable once the tenant column went server-owned (it compared the driver's
    /// own echoed user column against the acting tenant). The DEFECT it names is unchanged: the
    /// server took the client's word for the tenant. Only the leg it is observed on moved, from the
    /// driver's report to the orchestrator's Postgres probe.
    /// </summary>
    [Fact]
    public void Judge_StoredTenantIsTheOneTheClientSent_Idn003DerivationFails()
    {
        var assertions = JudgeHappy(observation: Derived(storedTenant: IdentityScenario.WrongTenantValue));

        Named(assertions, "acting user's own tenant").Passed.Should().BeFalse(
            "the server must force-set the tenant from the acting-user token, not take the client's word");
        Named(assertions, "acting user's own tenant").RequirementId.Should().Be("IVC-IDN-003");
    }

    // ── IVC-IDN-003's derivation half, and the control beside it ──────────────────────────────
    //
    // Two-sidedness is the whole point of this block. Direction 1: the server stops injecting the
    // column (or injects the wrong value) and the PROBE half must redden. Direction 2: the server
    // stops stripping the column outbound and the CONTROL half must redden. If either direction
    // stayed green the pair would be one-sided and would not do what the standard says it does.

    [Fact]
    public void JudgeTenantDerivation_ServerStoppedInjectingTheColumn_Idn003DerivationFails()
    {
        var assertions = JudgeHappy(observation: Derived(storedTenant: null));

        Named(assertions, "acting user's own tenant").Passed.Should().BeFalse(
            "a row with no server-owned tenant column at all is the injection having silently stopped");
        Named(assertions, "acting user's own tenant").RequirementId.Should().Be("IVC-IDN-003");
    }

    [Fact]
    public void JudgeTenantDerivation_GrpcReadCarriesTheServerOwnedColumn_TheControlFails()
    {
        var assertions = JudgeHappy(observation: Derived(
            grpcFields: ["Id", PostgresProbe.ServerOwnedTenantColumn, "OwnerId"]));

        var control = Named(assertions, "does not carry the server-owned tenant column");
        control.Passed.Should().BeFalse(
            "the outbound strip regressing must redden this cell, or the Postgres probe beside it is unconjoined");
    }

    [Fact]
    public void JudgeTenantDerivation_TheGrpcControl_CitesIdn004AndNotIdn003()
    {
        Named(JudgeHappy(), "does not carry the server-owned tenant column").RequirementId.Should()
            .Be(Requirements.IdnServerTenantColumnAbsentFromReadBack,
                "the gRPC-absent half grades the server's OUTBOUND STRIP — an EMISSION claim, which " +
                "is IVC-IDN-004 — and NOT IVC-IDN-003's DERIVATION statement; citing IVC-IDN-003 " +
                "here would silently widen that requirement to own a rule it does not make");

        // The negative half, stated so that it can actually FAIL ON ITS OWN. The `NotBe(IDN-003)`
        // this replaces could not: the `Be(...)` two lines above already implies it for as long as
        // the two consts hold different values — so it was true by construction and pinned
        // nothing. WHAT GUARANTEES THEY HOLD DIFFERENT VALUES, stated correctly at the third
        // attempt and verified by running both mutants:
        //   - REPOINTING this const at IVC-IDN-003 is caught, and caught FIRST by Check1.
        //     `ToHashSet()` collapses the duplicate on the REGISTRY side only; the standard still
        //     declares the orphaned IVC-IDN-004 row, so `registryIds` loses a member the standard
        //     keeps and Check1's `missingFromRegistry` is non-empty. Mutant DUP1 fails FOUR tests
        //     — Check1_ActiveIdsInStandard_ExactlyMatchConstsInRegistry, this test, Judge...
        //     _Idn004_IsCitedByExactlyOneAssertion, and Judge..._ProbeThrew_BothCitedAssertions
        //     FailNamingTheProbe. An earlier version of this comment said this case "survives
        //     every check in the gate"; that was FALSE (Ruling 49).
        //   - What genuinely survived was narrower: an ADDITIONAL const carrying the same value and
        //     no standard row of its own. Check1 balances (the value is already in both sets),
        //     Check2 matches IDENTIFIERS, which stay distinct, and Check3's uniqueness is over the
        //     DOCUMENT. Mutant DUP2 passed 442/442. That gap is now closed by
        //     RequirementsCoverageGateTests.RegistryConstValues_AreUniqueAcrossTheRegistry, which
        //     runs OnlyHaveUniqueItems over the reflected VALUES.
        // Counting IDN-003's citations across the
        // whole judgement is independently falsifiable in exactly the direction Ruling 14's caveat
        // cares about: re-point the strip control at IDN-003 and the count goes to FOUR; author a
        // fourth assertion that quietly takes IDN-003 and it goes to four as well. The baseline is
        // three, which is what the assertion below pins. Neither widening is
        // visible to the assertion above, and neither is visible to the coverage gate, whose
        // exactly-one rule counts LEDGER areas rather than code citations (Ruling 35).
        JudgeHappy().Count(a => a.RequirementId == Requirements.IdnTenancyDerivedAndEnforced)
            .Should().Be(3,
                "IVC-IDN-003's Statement has a DERIVATION half and an ENFORCEMENT half, and it is "
                + "graded by exactly three assertions and no others: the stored tenant being the "
                + "acting user's own, the client's value not having become it, and the wrong "
                + "acting user's update being denied. A FOURTH means some further claim has been "
                + "folded into that Statement");
    }

    /// <summary>
    /// IVC-IDN-004 must reach the cell, not merely exist as a const. Ruling 28 authored it because
    /// an assertion already observed the strip on every run while the standard recorded the area as
    /// Deferred — "no assertion observes it" — which was factually false. If this citation ever
    /// stops reaching a cell, the standard goes back to describing coverage it does not have.
    /// </summary>
    [Fact]
    public void JudgeTenantDerivation_Idn004_IsCitedByExactlyOneAssertion()
    {
        JudgeHappy().Count(a => a.RequirementId == Requirements.IdnServerTenantColumnAbsentFromReadBack)
            .Should().Be(1, "one observation, one emission claim graded from it");
    }

    [Fact]
    public void JudgeTenantDerivation_GrpcReadCasedDifferently_TheControlStillFails()
    {
        var assertions = JudgeHappy(observation: Derived(grpcFields: ["Id", "__tenantid"]));

        Named(assertions, "does not carry the server-owned tenant column").Passed.Should().BeFalse(
            "a re-cased leak is still a leak — the server's own reserved-name check is case-insensitive");
    }

    [Fact]
    public void JudgeTenantDerivation_DriversWrongValueOverwrittenInItsOwnColumn_TheNegativeControlFails()
    {
        var assertions = JudgeHappy(observation: Derived(storedUserColumn: Tenant));

        Named(assertions, "stayed in the client's own column").Passed.Should().BeFalse(
            "the driver's TenantId property exists to prove a user column with that name does NOT feed " +
            "the tenant boundary; a server that overwrote it is exactly the leak it guards");
        Named(assertions, "stayed in the client's own column").RequirementId.Should().Be("IVC-IDN-003");
    }

    /// <summary>
    /// RULING 16's control, stated at its sharpest: the leak clause
    /// (<c>storedTenant != WrongTenantValue</c>) is what makes this assertion say "the client's
    /// value became the row's tenant" rather than merely "the client's column still holds it".
    /// Deleting that clause survives every other test in this file, because no other fixture ever
    /// puts the wrong value in BOTH columns at once — a COPY rather than a move, which is exactly
    /// what a server that took the client's word for it would produce. The cell reddens either way
    /// (the derivation assertion beside it fails too), so this is cell-equivalent — but the mutant
    /// loses the DIAGNOSIS, and the diagnosis is the whole reason this control exists.
    /// </summary>
    [Fact]
    public void JudgeTenantDerivation_TheClientsValueBecameTheRowsTenantToo_TheNegativeControlFails()
    {
        var assertions = JudgeHappy(observation: Derived(
            storedTenant: IdentityScenario.WrongTenantValue,
            storedUserColumn: IdentityScenario.WrongTenantValue));

        Named(assertions, "stayed in the client's own column").Passed.Should().BeFalse(
            "the client's value sitting in BOTH columns is the server having COPIED it into the " +
            "tenant boundary — the precise leak this negative control exists to name, and it is " +
            "invisible to a check that only looks at the client's own column");
    }

    [Fact]
    public void JudgeTenantDerivation_TheDriversOwnTenantColumnIsGone_TheNegativeControlFails()
    {
        var assertions = JudgeHappy(observation: Derived(storedUserColumn: null));

        Named(assertions, "stayed in the client's own column").Passed.Should().BeFalse(
            "deleting the tenant_id property from the driver models takes the negative control with it, " +
            "and this is the assertion that says so out loud");
    }

    [Fact]
    public void JudgeTenantDerivation_ProbeThrew_BothCitedAssertionsFailNamingTheProbe()
    {
        var observation = new IdentityScenario.TenantObservation(
            Attempted: true, ProbeFailure: "NpgsqlException: connection refused",
            PostgresRow: null, GrpcFieldNames: ["Id"]);

        var assertions = IdentityScenario.JudgeTenantDerivation("dotnet", Tenant, observation);

        assertions.Where(a => a.RequirementId == "IVC-IDN-003").Should().HaveCount(2)
            .And.OnlyContain(a => !a.Passed);
        assertions.Should().Contain(a => a.Detail.Contains("connection refused", StringComparison.Ordinal),
            "a broken probe must be reported as a broken probe, never as a server defect");
        Named(assertions, "does not carry the server-owned tenant column").Passed.Should().BeTrue(
            "the strip half is a SEPARATE observation and a failed Postgres read says nothing about it — " +
            "conflating them would report one harness failure as two server defects");
    }

    [Fact]
    public void JudgeTenantDerivation_NoSeededRowSoNothingWasObserved_EveryAssertionFailsRatherThanBeingSkipped()
    {
        var assertions = IdentityScenario.JudgeTenantDerivation(
            "dotnet", Tenant, IdentityScenario.TenantObservation.NotAttempted);

        assertions.Should().HaveCount(3).And.OnlyContain(a => !a.Passed);
    }

    [Fact]
    public void JudgeTenantDerivation_TheServerBehaved_AllThreeAssertionsPass()
    {
        IdentityScenario.JudgeTenantDerivation("dotnet", Tenant, Derived())
            .Should().HaveCount(3).And.OnlyContain(a => a.Passed);
    }

    [Fact]
    public void Judge_WrongActingUsersUpdateWasDenied_Idn003EnforcementPasses()
    {
        var assertions = JudgeHappy();

        Named(assertions, "denied a write").Passed.Should().BeTrue();
        Named(assertions, "denied a write").RequirementId.Should().Be("IVC-IDN-003");
    }

    [Fact]
    public void Judge_WrongActingUsersUpdateSucceeded_Idn003EnforcementFails()
    {
        Named(JudgeHappy(denied: DeniedStep(null)), "denied a write").Passed.Should().BeFalse(
            "a wrong-tenant acting user whose write was accepted is exactly the defect this requirement names");
    }

    [Fact]
    public void Judge_WrongActingUsersUpdateFailedWithSomeOtherCode_Idn003EnforcementFails()
    {
        Named(JudgeHappy(denied: DeniedStep(16)), "denied a write").Passed.Should().BeFalse(
            "Unauthenticated (16) is a token problem, not the tenancy denial this requirement names");
    }

    [Fact]
    public void Judge_NoDeniedStepAtAll_Idn003EnforcementFailsRatherThanBeingSkipped()
    {
        var assertions = IdentityScenario.Judge("dotnet", Tenant, Owner, Key,
            ReadDocument(ReadStep(Key, Tenant, Owner)), Derived());

        Named(assertions, "denied a write").Passed.Should().BeFalse();
    }

    [Fact]
    public void Judge_DeniedStepItselfBroke_Idn003EnforcementFails()
    {
        var denied = new StepResult(IdentityScenario.DeniedStepName, false, Error: "channel closed");

        Named(JudgeHappy(denied: denied), "denied a write").Passed.Should().BeFalse(
            "a driver whose attempt never reached the server observed no denial");
    }

    /// <summary>
    /// The status NAME and MESSAGE ride in the assertion's detail as diagnostics, and nothing
    /// grades them — the server answers several distinct refusals on this path with the identical
    /// code AND message, which is exactly why they are reported rather than asserted on. This test
    /// exists so that carrying them cannot be dropped silently: they are the evidence that
    /// established the indistinguishability empirically, from what the driver itself received.
    /// </summary>
    [Fact]
    public void Judge_Idn003EnforcementDetail_CarriesTheReportedStatusNameAndMessage()
    {
        var denied = new StepResult(IdentityScenario.DeniedStepName, true,
            Entity: JsonSerializer.SerializeToElement(new
            {
                statusCode = IdentityScenario.DeniedStatusCode,
                status = "PermissionDenied",
                detail = "Not authorized to update this entity.",
            }));

        var detail = Named(JudgeHappy(denied: denied), "denied a write").Detail;

        detail.Should().Contain("PermissionDenied");
        detail.Should().Contain("Not authorized to update this entity.");
    }

    [Fact]
    public void Judge_DriverReportedNoStatusNameOrMessage_DetailSaysSoRatherThanThrowing()
    {
        var detail = Named(JudgeHappy(denied: DeniedStep(null)), "denied a write").Detail;

        detail.Should().Contain("<none>");
    }

    // ── every assertion fires on every language, whatever the driver reported ─────────────────

    [Fact]
    public void Judge_EmptyDocument_EveryIdnRequirementStillHasAFailingAssertion()
    {
        var assertions = IdentityScenario.Judge(
            "go", Tenant, Owner, null, ReadDocument(), IdentityScenario.TenantObservation.NotAttempted);

        assertions.Should().OnlyContain(a => !a.Passed);
        Cited(assertions, "IVC-IDN-002").Should().NotBeEmpty();
        Cited(assertions, "IVC-IDN-003").Should().NotBeEmpty();
        assertions.Should().OnlyContain(a => a.Name.StartsWith("go: ", StringComparison.Ordinal));
    }

    // ── the pure readers ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void SeededKey_ReadsThisLanguagesRowKeyOnly()
    {
        var keys = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["dotnet"] = new Dictionary<string, string> { [IdentityScenario.RowKeyName] = Key.ToString() },
            ["go"] = new Dictionary<string, string> { ["something_else"] = OtherKey.ToString() },
        };

        IdentityScenario.SeededKey(keys, "dotnet").Should().Be(Key);
        IdentityScenario.SeededKey(keys, "go").Should().BeNull();
        IdentityScenario.SeededKey(keys, "java").Should().BeNull();
    }

    [Fact]
    public void SeededKey_UnparsableKey_IsNull()
    {
        var keys = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["dotnet"] = new Dictionary<string, string> { [IdentityScenario.RowKeyName] = "not-a-uuid" },
        };

        IdentityScenario.SeededKey(keys, "dotnet").Should().BeNull();
    }

    [Fact]
    public void ReadString_MalformedDocuments_YieldNullRatherThanThrowing()
    {
        IdentityScenario.ReadString(null, "tenant").Should().BeNull();
        IdentityScenario.ReadString(JsonSerializer.SerializeToElement("scalar"), "tenant").Should().BeNull();
        IdentityScenario.ReadString(JsonSerializer.SerializeToElement(new { tenant = 7 }), "tenant").Should().BeNull();
        IdentityScenario.ReadString(JsonSerializer.SerializeToElement(new { tenant = "t" }), "tenant").Should().Be("t");
    }

    [Fact]
    public void ReadStatusCode_MalformedDocuments_YieldNullRatherThanThrowing()
    {
        IdentityScenario.ReadStatusCode(null).Should().BeNull();
        IdentityScenario.ReadStatusCode(JsonSerializer.SerializeToElement(new { statusCode = (int?)null })).Should().BeNull();
        IdentityScenario.ReadStatusCode(JsonSerializer.SerializeToElement(new { statusCode = "7" })).Should().BeNull(
            "a status code reported as a string is not a numeric code, and coercing it would invent agreement");
        IdentityScenario.ReadStatusCode(JsonSerializer.SerializeToElement(new { statusCode = 7 })).Should().Be(7);
    }

    // ── the negative leg's own precondition ───────────────────────────────────────────────────

    [Fact]
    public void PreconditionFailure_TwoIdentitiesInDifferentTenants_IsUsable()
    {
        IdentityScenario.PreconditionFailure("tenant_bypass", "tenant_smoke_test", "a-token")
            .Should().BeNull();
    }

    [Fact]
    public void PreconditionFailure_BothIdentitiesShareATenant_IsRefused()
    {
        IdentityScenario.PreconditionFailure("tenant_bypass", "tenant_bypass", "a-token")
            .Should().Contain("tenant_bypass",
                "a 'wrong' acting user in the SAME tenant would be denied for ownership, or not at all — " +
                "either way the cell would not be evidence about tenancy");
    }

    [Fact]
    public void PreconditionFailure_NoWrongActingTokenAtAll_IsRefused()
    {
        IdentityScenario.PreconditionFailure("tenant_bypass", "tenant_smoke_test", "")
            .Should().NotBeNull(
                "with no second token the drivers would send their own, and the update would be ALLOWED");
    }

    // ── register-phase capture ────────────────────────────────────────────────────────────────

    [Fact]
    public void TryCaptureDescriptor_NoRegisterStep_IsAFailureNotASilentNull()
    {
        var (descriptor, failure) = IdentityScenario.TryCaptureDescriptor(
            new PhaseDocument("dotnet", "register", [new StepResult("something_else", true)]));

        descriptor.Should().BeNull();
        failure.Should().Contain(IdentityScenario.RegisterStepName);
    }

    [Fact]
    public void TryCaptureDescriptor_RegisterStepFailed_ReportsTheDriversOwnError()
    {
        var (descriptor, failure) = IdentityScenario.TryCaptureDescriptor(
            new PhaseDocument("dotnet", "register",
                [new StepResult(IdentityScenario.RegisterStepName, false, Error: "boom")]));

        descriptor.Should().BeNull();
        failure.Should().Be("boom");
    }

    // ── RunAsync plumbing and the read-phase grading seam ─────────────────────────────────────
    //
    // Everything above judges. NOTHING above proved the judgement ever REACHES a cell — this file
    // had zero RunAsync coverage, and deleting the Judge wiring in RunAsync left the whole suite
    // green while the scenario verified nothing. GradeReads is that wiring, extracted so it is
    // callable without a live stack; the tests below are what redden when it is dropped.
    // RunAsync's own phase plumbing is exercised the way SchemaCatalogScenarioTests exercises its
    // own: repoRoot "/tmp" has no driver project, so every driver breaks loudly and predictably.

    private static DriverContext Context(string wrongActingToken = "wrong-acting-token") => new(
        Scenario: IdentityScenario.Name,
        Type: string.Empty,
        Tenant: "iverson-loadtest-dynamic",
        GrpcUrl: "http://localhost:5000",
        ClientId: "client-id",
        ClientSecret: "client-secret",
        TokenEndpoint: "http://localhost:9000/application/o/token/",
        ActingToken: "acting-token",
        OwnerId: "owner-id",
        IdPrefix: "s8-",
        WrongActingToken: wrongActingToken);

    /// <summary>
    /// The channel points at a port nothing is listening on and the probe's connection string is
    /// empty, on purpose: every leg the scenario tries to observe fails LOUDLY and locally, which
    /// is what lets the wiring tests below run with no stack at all. A test that needs a
    /// SUCCESSFUL observation supplies it as a <see cref="IdentityScenario.TenantObservation"/>
    /// instead — that is exactly why the observation is a value the judgement takes rather than
    /// something it fetches.
    /// </summary>
    private static IdentityScenario BuildScenario(string repoRoot = "/tmp", DriverRunner? runner = null)
    {
        var channel = Grpc.Net.Client.GrpcChannel.ForAddress("http://localhost:1");
        return new IdentityScenario(
            runner ?? new DriverRunner(repoRoot: repoRoot),
            new Reregistrar(new Iverson.Client.Contracts.ObjectMappingService.ObjectMappingServiceClient(channel)),
            new Iverson.Client.Contracts.ObjectMappingService.ObjectMappingServiceClient(channel),
            new PostgresProbe(string.Empty));
    }

    private static Dictionary<string, IdentityScenario.LanguageState> States(params string[] languages) =>
        languages.ToDictionary(l => l, _ => new IdentityScenario.LanguageState(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// THE mutation this test exists for: deleting the <c>Judge</c> call inside
    /// <c>GradeReads</c>. That used to leave every suite green while no IDN assertion reached a
    /// cell at all.
    /// </summary>
    [Fact]
    public async Task GradeReads_TheReadPhaseJudgement_ReachesTheCellCarryingItsIdnCitations()
    {
        // The runner must actually hold a seeded key for "dotnet", or SeededKey returns null and
        // the real ObserveTenantAsync short-circuits to NotAttempted — which would make the
        // wiring mutation below indistinguishable from the truth.
        var runner = new DriverRunner(repoRoot: "/tmp");
        runner.MergeKeys("dotnet", new PhaseDocument("dotnet", "write",
        [
            new StepResult(IdentityScenario.WriteStepName, true,
                Keys: new Dictionary<string, string> { [IdentityScenario.RowKeyName] = Key.ToString() }),
        ]));

        var cells = await BuildScenario(runner: runner).GradeReadsAsync(States("dotnet"),
            [("dotnet", ReadDocument(ReadStep(Key, Tenant, Owner), DeniedStep(IdentityScenario.DeniedStatusCode)))],
            Tenant, Owner, "acting-token");

        var cell = cells.Should().ContainSingle().Subject;
        cell.Scenario.Should().Be(IdentityScenario.Name);
        cell.Assertions.Should().Contain(a => a.RequirementId == Requirements.IdnTenancyDerivedAndEnforced);

        // THE SECOND wiring mutation, one layer in: replacing the ObserveTenantAsync call with
        // TenantObservation.NotAttempted. IVC-IDN-003's derivation half would then be graded
        // against a value nothing observed, and — because it still fails and still cites the right
        // id — every assertion above stays green through it. What separates the two is the DETAIL:
        // a real observation against this test's dead endpoints reports the probe FAILING, while
        // NotAttempted reports that no key was seeded. The seeded key here is real, so "no row key"
        // is the one thing this cell must never say.
        cell.Assertions.Should().NotContain(
            a => a.RequirementId == Requirements.IdnTenancyDerivedAndEnforced
                 && a.Detail.Contains("reported no row key", StringComparison.Ordinal),
            "the orchestrator must actually ATTEMPT the observation for a row it seeded");
        cell.Assertions.Should().Contain(a => a.Detail.Contains("the Postgres probe failed", StringComparison.Ordinal),
            "which is what an attempted probe against this test's dead connection string reports");
    }

    /// <summary>The same mutation, one phase earlier: dropping the <c>JudgeWrite</c> wiring.</summary>
    [Fact]
    public void GradeWrites_TheWritePhaseJudgement_ReachesTheLanguagesState()
    {
        var states = States("dotnet");

        IdentityScenario.GradeWrites(states,
            [("dotnet", new PhaseDocument("dotnet", "write", [new StepResult(IdentityScenario.WriteStepName, true)]))]);

        states["dotnet"].Assertions.Should().Contain(a => a.RequirementId == Requirements.IdnDualIdentityAcceptedOnWrite);
    }

    [Fact]
    public async Task GradeReads_ALanguageWhoseDriverReportedNoReadDocument_IsNotGreen()
    {
        var cells = await BuildScenario().GradeReadsAsync(States("dotnet", "go"),
            [("dotnet", ReadDocument(ReadStep(Key, Tenant, Owner), DeniedStep(IdentityScenario.DeniedStatusCode)))],
            Tenant, Owner, "acting-token");

        cells.Single(c => c.Language == "go").Status.Should().NotBe(CellStatus.Ok);
    }

    [Fact]
    public async Task RunAsync_NoLanguagesRequested_ReturnsNoCells() =>
        (await BuildScenario().RunAsync([], Context(), "acting-token", "other-tenant")).Should().BeEmpty();

    [Fact]
    public async Task RunAsync_NoWrongActingUserToken_FailsEveryRowWithThePreconditionReason()
    {
        var cells = await BuildScenario().RunAsync(
            ["dotnet", "python"], Context(wrongActingToken: string.Empty), "acting-token", "other-tenant");

        cells.Should().HaveCount(2);
        cells.Should().OnlyContain(c => c.Status == CellStatus.Fail);
        cells.Should().OnlyContain(c => c.Detail!.Contains("no wrong-acting-user token"));
    }

    [Fact]
    public async Task RunAsync_TheRegisterDriverBreaks_FailsEveryRequestedLanguage()
    {
        var cells = await BuildScenario().RunAsync(
            ["dotnet", "python"], Context(), "acting-token", "other-tenant");

        cells.Should().HaveCount(2);
        cells.Should().NotContain(c => c.Status == CellStatus.Ok);
        cells.Should().OnlyContain(c => c.Scenario == IdentityScenario.Name);
    }
}
