using FluentAssertions;
using Xunit;

namespace Iverson.Api.Tests;

public class SchemaAdminAuthorizationPolicyTests
{
    [Fact]
    public void IsSatisfiedBy_HumanInOperatorsGroup_ReturnsTrue()
    {
        SchemaAdminAuthorizationPolicy.IsSatisfiedBy(["operators"], null).Should().BeTrue();
    }

    [Fact]
    public void IsSatisfiedBy_HumanInOtherGroupOnly_ReturnsFalse()
    {
        SchemaAdminAuthorizationPolicy.IsSatisfiedBy(["some-other-group"], null).Should().BeFalse();
    }

    [Fact]
    public void IsSatisfiedBy_AutomationWithSchemaAdminScope_ReturnsTrue()
    {
        SchemaAdminAuthorizationPolicy.IsSatisfiedBy([], "openid schema_admin profile").Should().BeTrue();
    }

    [Fact]
    public void IsSatisfiedBy_AutomationWithAdminScopeAlone_ReturnsFalse()
    {
        // "admin" is the existing (broader) Operator scope — it must NOT satisfy this narrower
        // policy on its own, since that would silently widen every "admin"-scoped automation
        // client's rights to schema registration too.
        SchemaAdminAuthorizationPolicy.IsSatisfiedBy([], "openid admin profile").Should().BeFalse();
    }

    [Fact]
    public void IsSatisfiedBy_AutomationWithoutSchemaAdminScope_ReturnsFalse()
    {
        SchemaAdminAuthorizationPolicy.IsSatisfiedBy([], "openid profile").Should().BeFalse();
    }

    [Fact]
    public void IsSatisfiedBy_NoGroupsNoScope_ReturnsFalse()
    {
        SchemaAdminAuthorizationPolicy.IsSatisfiedBy([], null).Should().BeFalse();
    }
}
