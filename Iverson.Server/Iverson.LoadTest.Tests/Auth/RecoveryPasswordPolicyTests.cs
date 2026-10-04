using FluentAssertions;
using Iverson.LoadTest.Auth;
using Xunit;

namespace Iverson.LoadTest.Tests.Auth;

public class RecoveryPasswordPolicyTests
{
    [Theory]
    [InlineData("", true)]
    [InlineData("1234567", true)]
    [InlineData("12345678", false)]
    public void IsTooShort_RejectsPasswordsUnderEightCharacters(string password, bool expected) =>
        RecoveryPasswordPolicy.IsTooShort(password).Should().Be(expected);
}
