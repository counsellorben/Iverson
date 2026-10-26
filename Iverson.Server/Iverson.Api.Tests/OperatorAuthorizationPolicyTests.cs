using System.Security.Claims;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Iverson.Api.Tests;

/// <summary>
/// CSR round 12 §2.4: Operator comes from this install's operator list (a principal's <c>sub</c>), from an
/// <c>admin</c>-scoped automation token, or, on compose only, from membership of the dev fallback group.
/// The <c>groups</c> claim yields Operator nowhere else.
/// </summary>
public class OperatorAuthorizationPolicyTests
{
    internal const string ListedSubject = "listed-operator-sub";

    internal static readonly OperatorSubjects ListOnly =
        new(new HashSet<string> { ListedSubject }, GroupFallback: null);

    internal static readonly OperatorSubjects ListAndComposeFallback =
        new(new HashSet<string> { ListedSubject }, GroupFallback: "operators");

    /// <summary>A principal as the JwtBearer handler builds it: one claim per <c>groups</c> element.</summary>
    internal static ClaimsPrincipal Principal(string? sub, string? scope = null, params string[] groups)
    {
        var claims = new List<Claim>();
        if (sub is not null)
            claims.Add(new Claim("sub", sub));
        if (scope is not null)
            claims.Add(new Claim("scope", scope));
        claims.AddRange(groups.Select(g => new Claim("groups", g)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public void IsSatisfiedBy_SubjectOnTheList_ReturnsTrue()
    {
        OperatorAuthorizationPolicy.IsSatisfiedBy(Principal(ListedSubject), ListOnly).Should().BeTrue();
    }

    [Fact]
    public void IsSatisfiedBy_SubjectNotOnTheList_ReturnsFalse()
    {
        OperatorAuthorizationPolicy.IsSatisfiedBy(Principal("someone-else"), ListOnly).Should().BeFalse();
    }

    [Fact]
    public void IsSatisfiedBy_OperatorsGroupWithoutTheComposeFallback_ReturnsFalse()
    {
        OperatorAuthorizationPolicy.IsSatisfiedBy(Principal("someone-else", null, "operators"), ListOnly)
            .Should().BeFalse("a group membership the orchestrator can write must not yield Operator");
    }

    [Fact]
    public void IsSatisfiedBy_ComposeFallbackGroupMember_ReturnsTrue()
    {
        OperatorAuthorizationPolicy.IsSatisfiedBy(Principal("someone-else", null, "operators"), ListAndComposeFallback)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("some-other-group")]
    [InlineData("non-operators")]
    public void IsSatisfiedBy_ComposeFallbackSet_ButNotAMemberOfThatGroup_ReturnsFalse(string group)
    {
        OperatorAuthorizationPolicy.IsSatisfiedBy(Principal("someone-else", null, group), ListAndComposeFallback)
            .Should().BeFalse();
    }

    [Fact]
    public void IsSatisfiedBy_AutomationWithAdminScope_ReturnsTrue()
    {
        OperatorAuthorizationPolicy.IsSatisfiedBy(Principal("ak-automation", "openid admin profile"), ListOnly)
            .Should().BeTrue();
    }

    [Fact]
    public void IsSatisfiedBy_AutomationWithoutAdminScope_ReturnsFalse()
    {
        OperatorAuthorizationPolicy.IsSatisfiedBy(Principal("ak-automation", "openid profile"), ListOnly)
            .Should().BeFalse();
    }

    [Fact]
    public void IsSatisfiedBy_NoSubNoScopeNoGroups_ReturnsFalse()
    {
        OperatorAuthorizationPolicy.IsSatisfiedBy(Principal(null), ListAndComposeFallback).Should().BeFalse();
    }

    [Fact]
    public void FromConfiguration_ReadsTheListAndTheFallback()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authorization:OperatorSubjects:0"] = "sub-a",
            ["Authorization:OperatorSubjects:1"] = "sub-b",
            ["Authorization:OperatorGroupFallback"] = "operators",
        }).Build();

        var subjects = OperatorSubjects.FromConfiguration(configuration);

        subjects.Subjects.Should().BeEquivalentTo("sub-a", "sub-b");
        subjects.GroupFallback.Should().Be("operators");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FromConfiguration_NothingSet_IsAnEmptyListAndNoFallback(string? fallback)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authorization:OperatorGroupFallback"] = fallback,
        }).Build();

        var subjects = OperatorSubjects.FromConfiguration(configuration);

        subjects.Subjects.Should().BeEmpty();
        subjects.GroupFallback.Should().BeNull("an empty setting must not make members of a group named \"\" Operators");
    }
}
