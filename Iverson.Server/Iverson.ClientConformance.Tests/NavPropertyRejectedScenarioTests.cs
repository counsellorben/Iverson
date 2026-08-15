using FluentAssertions;
using Grpc.Core;
using Iverson.ClientConformance.Scenarios;
using Xunit;

namespace Iverson.ClientConformance.Tests;

public class NavPropertyRejectedScenarioTests
{
    [Fact]
    public void Judge_ServerRejectsWithInvalidArgumentNamingBothTerms_AllPass()
    {
        var caught = new RpcException(new Status(
            StatusCode.InvalidArgument,
            "Relation 'Author' is a navigation property and cannot be written — send " +
            "'AuthorId' instead."));

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
            "'AuthorId' instead."));

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
}
