using FluentAssertions;
using Iverson.Api;
using Iverson.StarRocks;
using Xunit;

namespace Iverson.Api.Tests;

public class ReadinessPolicyTests
{
    [Fact]
    public void Evaluate_AllHealthyIncludingStarRocks_IsReadyAndFullyHealthy()
    {
        var result = ReadinessPolicy.Evaluate(true, StarRocksHealthStatus.Healthy, true, true);

        result.Ready.Should().BeTrue();
        result.FullyHealthy.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_StarRocksAuthPending_IsReadyButNotFullyHealthy()
    {
        var result = ReadinessPolicy.Evaluate(true, StarRocksHealthStatus.AuthPending, true, true);

        result.Ready.Should().BeTrue();
        result.FullyHealthy.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_StarRocksUnhealthy_IsNotReady()
    {
        var result = ReadinessPolicy.Evaluate(true, StarRocksHealthStatus.Unhealthy, true, true);

        result.Ready.Should().BeFalse();
        result.FullyHealthy.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void Evaluate_AnyOtherDependencyUnhealthy_IsNotReady(bool postgres, bool qdrant, bool kafka)
    {
        var result = ReadinessPolicy.Evaluate(postgres, StarRocksHealthStatus.Healthy, qdrant, kafka);

        result.Ready.Should().BeFalse();
    }
}
