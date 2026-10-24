using FluentAssertions;
using Xunit;
using static Iverson.Api.Tests.OperatorAuthorizationPolicyTests;

namespace Iverson.Api.Tests;

/// <summary>
/// CSR round 12 §2.4: SchemaAdmin is Operator (the list, the <c>admin</c> scope or compose's group fallback)
/// or the <c>schema_admin</c> scope, so "Operator implies SchemaAdmin" holds for every Operator shape.
/// </summary>
public class SchemaAdminAuthorizationPolicyTests
{
    [Fact]
    public void IsSatisfiedBy_SubjectOnTheOperatorList_ReturnsTrue()
    {
        SchemaAdminAuthorizationPolicy.IsSatisfiedBy(Principal(ListedSubject), ListOnly).Should().BeTrue();
    }

    [Fact]
    public void IsSatisfiedBy_OperatorsGroupWithoutTheComposeFallback_ReturnsFalse()
    {
        SchemaAdminAuthorizationPolicy.IsSatisfiedBy(Principal("someone-else", null, "operators"), ListOnly)
            .Should().BeFalse();
    }

    [Fact]
    public void IsSatisfiedBy_ComposeFallbackGroupMember_ReturnsTrue()
    {
        SchemaAdminAuthorizationPolicy.IsSatisfiedBy(Principal("someone-else", null, "operators"), ListAndComposeFallback)
            .Should().BeTrue();
    }

    [Fact]
    public void IsSatisfiedBy_AutomationWithSchemaAdminScope_ReturnsTrue()
    {
        SchemaAdminAuthorizationPolicy.IsSatisfiedBy(Principal("ak-loadtest", "openid schema_admin profile"), ListOnly)
            .Should().BeTrue();
    }

    [Fact]
    public void IsSatisfiedBy_AutomationWithAdminScope_ReturnsTrue()
    {
        // The admin scope makes its holder an Operator, and Operator implies SchemaAdmin.
        SchemaAdminAuthorizationPolicy.IsSatisfiedBy(Principal("ak-automation", "openid admin profile"), ListOnly)
            .Should().BeTrue();
    }

    [Fact]
    public void IsSatisfiedBy_AutomationWithNeitherScope_ReturnsFalse()
    {
        SchemaAdminAuthorizationPolicy.IsSatisfiedBy(Principal("ak-other", "openid profile"), ListOnly)
            .Should().BeFalse();
    }

    [Fact]
    public void IsSatisfiedBy_NoSubNoScopeNoGroups_ReturnsFalse()
    {
        SchemaAdminAuthorizationPolicy.IsSatisfiedBy(Principal(null), ListAndComposeFallback).Should().BeFalse();
    }
}
