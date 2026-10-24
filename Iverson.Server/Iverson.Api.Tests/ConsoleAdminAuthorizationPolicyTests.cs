using FluentAssertions;
using Xunit;
using static Iverson.Api.Tests.OperatorAuthorizationPolicyTests;

namespace Iverson.Api.Tests;

/// <summary>CSR round 12 §2.2: the console's audience is Operators and tenant admins, nobody else.</summary>
public class ConsoleAdminAuthorizationPolicyTests
{
    [Fact]
    public void IsSatisfiedBy_SubjectOnTheOperatorList_ReturnsTrue()
    {
        ConsoleAdminAuthorizationPolicy.IsSatisfiedBy(Principal(ListedSubject), ListOnly).Should().BeTrue();
    }

    [Fact]
    public void IsSatisfiedBy_TenantAdmin_ReturnsTrue()
    {
        ConsoleAdminAuthorizationPolicy.IsSatisfiedBy(Principal("tenant-admin-sub", null, "tenant-admins"), ListOnly)
            .Should().BeTrue();
    }

    [Fact]
    public void IsSatisfiedBy_OperatorsGroupWithoutTheComposeFallback_ReturnsFalse()
    {
        ConsoleAdminAuthorizationPolicy.IsSatisfiedBy(Principal("someone-else", null, "operators"), ListOnly)
            .Should().BeFalse();
    }

    [Fact]
    public void IsSatisfiedBy_SignedInUserWithNeitherRole_ReturnsFalse()
    {
        ConsoleAdminAuthorizationPolicy.IsSatisfiedBy(Principal("ordinary-user-sub", null, "some-other-group"), ListOnly)
            .Should().BeFalse();
    }
}
