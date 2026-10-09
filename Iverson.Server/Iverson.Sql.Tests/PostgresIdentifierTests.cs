using FluentAssertions;
using Xunit;

namespace Iverson.Sql.Tests;

public class PostgresIdentifierTests
{
    [Fact]
    public void Quote_WrapsTheNameInDoubleQuotes()
    {
        PostgresIdentifier.Quote("articles").Should().Be("\"articles\"");
        // The server-owned tenant column fails the client pattern but is a legal quoted name.
        PostgresIdentifier.Quote("__TenantId").Should().Be("\"__TenantId\"");
    }

    [Fact]
    public void Quote_NameContainingADoubleQuote_IsRefused()
    {
        var act = () => PostgresIdentifier.Quote("Na\"me");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Quote_NameContainingANulCharacter_IsRefused()
    {
        var act = () => PostgresIdentifier.Quote("Na\0me");

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("Title", true)]
    [InlineData("Widget2", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("Two Words", false)]
    [InlineData("snake_case", false)]
    [InlineData("2Title", false)]
    [InlineData("Title\n", false)]
    public void IsPlain_AcceptsOnlyALetterFollowedByLettersAndDigits(string? name, bool expected) =>
        PostgresIdentifier.IsPlain(name).Should().Be(expected);
}
