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
}
