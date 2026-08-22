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

    private static IReadOnlyList<Assertion> JudgeHappy(
        StepResult? read = null, StepResult? denied = null, Guid? seeded = null) =>
        IdentityScenario.Judge("dotnet", Tenant, Owner, seeded ?? Key,
            ReadDocument(read ?? ReadStep(Key, Tenant, Owner), denied ?? DeniedStep(IdentityScenario.DeniedStatusCode)));

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
            ReadDocument(ReadStep(Key, Tenant, Owner), DeniedStep(IdentityScenario.DeniedStatusCode)));

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
            ReadDocument(DeniedStep(IdentityScenario.DeniedStatusCode)));

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

    [Fact]
    public void Judge_StoredTenantIsTheOneTheClientSent_Idn003DerivationFails()
    {
        var assertions = JudgeHappy(read: ReadStep(Key, IdentityScenario.WrongTenantValue, Owner));

        Named(assertions, "acting user's own tenant").Passed.Should().BeFalse(
            "the server must force-set the tenant from the acting-user token, not take the client's word");
        Named(assertions, "acting user's own tenant").RequirementId.Should().Be("IVC-IDN-003");
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
            ReadDocument(ReadStep(Key, Tenant, Owner)));

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
        var assertions = IdentityScenario.Judge("go", Tenant, Owner, null, ReadDocument());

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
}
