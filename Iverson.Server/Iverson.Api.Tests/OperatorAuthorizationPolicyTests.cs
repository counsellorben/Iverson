using FluentAssertions;
using Xunit;

namespace Iverson.Api.Tests;

public class OperatorAuthorizationPolicyTests
{
    [Fact]
    public void IsSatisfiedBy_HumanInOperatorsGroup_ReturnsTrue()
    {
        OperatorAuthorizationPolicy.IsSatisfiedBy(["operators"], null).Should().BeTrue();
    }

    [Fact]
    public void IsSatisfiedBy_HumanInOtherGroupOnly_ReturnsFalse()
    {
        OperatorAuthorizationPolicy.IsSatisfiedBy(["some-other-group"], null).Should().BeFalse();
    }

    [Fact]
    public void IsSatisfiedBy_AutomationWithAdminScope_ReturnsTrue()
    {
        OperatorAuthorizationPolicy.IsSatisfiedBy([], "openid admin profile").Should().BeTrue();
    }

    [Fact]
    public void IsSatisfiedBy_AutomationWithoutAdminScope_ReturnsFalse()
    {
        OperatorAuthorizationPolicy.IsSatisfiedBy([], "openid profile").Should().BeFalse();
    }

    [Fact]
    public void IsSatisfiedBy_NoGroupsNoScope_ReturnsFalse()
    {
        OperatorAuthorizationPolicy.IsSatisfiedBy([], null).Should().BeFalse();
    }
}
