using FluentAssertions;
using Xunit;

namespace Iverson.Api.Tests;

public class ConsoleClientAuthorizationPolicyTests
{
    [Fact]
    public void IsSatisfiedBy_TokenIssuedToTheConsole_ReturnsTrue()
    {
        ConsoleClientAuthorizationPolicy.IsSatisfiedBy(["console-client-id"], "console-client-id").Should().BeTrue();
    }

    [Fact]
    public void IsSatisfiedBy_TokenIssuedToAnotherClient_ReturnsFalse()
    {
        ConsoleClientAuthorizationPolicy.IsSatisfiedBy(["service-client-id"], "console-client-id").Should().BeFalse();
    }

    [Fact]
    public void IsSatisfiedBy_AudienceComparedOrdinally_ReturnsFalseForACaseVariant()
    {
        ConsoleClientAuthorizationPolicy.IsSatisfiedBy(["Console-Client-Id"], "console-client-id").Should().BeFalse();
    }

    // Fail closed: an unset console audience matches nothing, not even an empty aud claim.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsSatisfiedBy_ConsoleAudienceUnset_ReturnsFalse(string? consoleAudience)
    {
        ConsoleClientAuthorizationPolicy.IsSatisfiedBy(["", "console-client-id"], consoleAudience).Should().BeFalse();
    }
}
