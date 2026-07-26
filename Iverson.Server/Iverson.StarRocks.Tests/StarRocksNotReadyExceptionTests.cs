using FluentAssertions;
using Iverson.StarRocks;
using Xunit;

namespace Iverson.StarRocks.Tests;

public class EngagementNotReadyExceptionTests
{
    [Fact]
    public void Constructor_SetsMessageAndInnerException()
    {
        var inner = new InvalidOperationException("boom");

        var ex = new EngagementNotReadyException("not ready", inner);

        ex.Message.Should().Be("not ready");
        ex.InnerException.Should().BeSameAs(inner);
    }

    [Fact]
    public void Constructor_InnerExceptionIsOptional()
    {
        var ex = new EngagementNotReadyException("not ready");

        ex.Message.Should().Be("not ready");
        ex.InnerException.Should().BeNull();
    }
}
