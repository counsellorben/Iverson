using FluentAssertions;
using Xunit;

namespace Iverson.StarRocks.Tests;

public class DisabledEngagementStoreHealthCheckTests
{
    // The whole point of this class, unlike its sibling DisabledEngagementStoreSearchService
    // (which deliberately throws): /health calls CheckHealthAsync() on every request via
    // Task.WhenAll, and the api/worker Deployments' readinessProbe polls /health. A throwing
    // implementation here would make the pod permanently unready on the
    // engagementEnabled: false profile (values-laptop.yaml), so both members must return a
    // benign result rather than throw.
    [Fact]
    public async Task CheckHealthAsync_DoesNotThrow_AndReturnsHealthy()
    {
        var sut = new DisabledEngagementStoreHealthCheck();

        var result = await sut.CheckHealthAsync();

        result.Should().Be(EngagementHealthStatus.Healthy);
    }

    [Fact]
    public async Task IsHealthyAsync_DoesNotThrow_AndReturnsTrue()
    {
        var sut = new DisabledEngagementStoreHealthCheck();

        var result = await sut.IsHealthyAsync();

        result.Should().BeTrue();
    }

    [Fact]
    public void DisabledEngagementStoreHealthCheck_ImplementsIEngagementStoreHealthCheck()
    {
        typeof(DisabledEngagementStoreHealthCheck).Should().Implement<IEngagementStoreHealthCheck>();
    }
}
