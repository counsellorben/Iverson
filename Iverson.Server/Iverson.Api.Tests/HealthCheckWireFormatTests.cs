using System.Text.Json;
using FluentAssertions;
using Iverson.Api;
using Iverson.StarRocks;
using Xunit;

namespace Iverson.Api.Tests;

/// <summary>
/// Pins the four values <c>/health</c>'s <c>checks.starrocks</c> may take, and the one that
/// matters most: <see cref="EngagementHealthStatus.AuthPending"/> must not collapse to
/// <c>false</c>.
/// </summary>
public class HealthCheckWireFormatTests
{
    [Fact]
    public void StarRocksCheck_Healthy_IsTrue()
    {
        HealthCheckWireFormat.StarRocksCheck(EngagementHealthStatus.Healthy, engagementEnabled: true)
            .Should().Be(true);
    }

    [Fact]
    public void StarRocksCheck_Unhealthy_IsFalse()
    {
        HealthCheckWireFormat.StarRocksCheck(EngagementHealthStatus.Unhealthy, engagementEnabled: true)
            .Should().Be(false);
    }

    /// <summary>
    /// The seam this file exists for. <c>ReadinessPolicy.Evaluate</c> deliberately reports
    /// <c>AuthPending</c> as READY — a fresh install would otherwise deadlock on its own
    /// post-install hook — so <c>/health</c> answers 200 while this check is not "up". Flattened
    /// to a boolean it becomes <c>false</c>, which the console can only render as a red "Down"
    /// chip on a deployment that is progressing correctly.
    /// </summary>
    [Fact]
    public void StarRocksCheck_AuthPending_IsItsOwnValueAndIsNotFalse()
    {
        var value = HealthCheckWireFormat.StarRocksCheck(
            EngagementHealthStatus.AuthPending, engagementEnabled: true);

        value.Should().Be("authPending");
        value.Should().NotBe(false);
    }

    [Theory]
    [InlineData(EngagementHealthStatus.Healthy)]
    [InlineData(EngagementHealthStatus.AuthPending)]
    [InlineData(EngagementHealthStatus.Unhealthy)]
    public void StarRocksCheck_EngagementDisabled_IsDisabledWhateverTheStatusSays(
        EngagementHealthStatus status)
    {
        // A disabled store is never probed, so whatever status the fan-out recorded says nothing.
        HealthCheckWireFormat.StarRocksCheck(status, engagementEnabled: false)
            .Should().Be("disabled");
    }

    /// <summary>
    /// The four values as they actually serialize. The projection returns <see cref="object"/>,
    /// so this is what pins that a boolean stays a JSON boolean and does not become the string
    /// <c>"True"</c> — the failure mode a boxed value invites.
    /// </summary>
    [Theory]
    [InlineData(EngagementHealthStatus.Healthy, true, "true")]
    [InlineData(EngagementHealthStatus.Unhealthy, true, "false")]
    [InlineData(EngagementHealthStatus.AuthPending, true, "\"authPending\"")]
    [InlineData(EngagementHealthStatus.Healthy, false, "\"disabled\"")]
    public void StarRocksCheck_SerializesAsTheDocumentedJson(
        EngagementHealthStatus status, bool engagementEnabled, string expectedJson)
    {
        var checks = new { starrocks = HealthCheckWireFormat.StarRocksCheck(status, engagementEnabled) };

        JsonSerializer.Serialize(checks).Should().Be($$"""{"starrocks":{{expectedJson}}}""");
    }
}
