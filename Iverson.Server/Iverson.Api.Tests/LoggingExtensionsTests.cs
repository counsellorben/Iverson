using FluentAssertions;
using Iverson.Api;
using Xunit;

namespace Iverson.Api.Tests;

public class LoggingExtensionsTests
{
    [Fact]
    public void SanitizeForLog_RemovesNewlines()
    {
        var input = "Foo\nBar";
        var result = input.SanitizeForLog();
        result.Should().Be("FooBar");
    }

    [Fact]
    public void SanitizeForLog_RemovesCarriageReturns()
    {
        var input = "Foo\rBar";
        var result = input.SanitizeForLog();
        result.Should().Be("FooBar");
    }

    [Fact]
    public void SanitizeForLog_RemovesBothNewlineAndCarriageReturn()
    {
        var input = "Foo\r\nBar";
        var result = input.SanitizeForLog();
        result.Should().Be("FooBar");
    }

    [Fact]
    public void SanitizeForLog_PreservesNormalText()
    {
        var input = "FooBar";
        var result = input.SanitizeForLog();
        result.Should().Be("FooBar");
    }
}
