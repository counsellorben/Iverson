using FluentAssertions;
using Iverson.StarRocks;
using Xunit;

namespace Iverson.StarRocks.Tests;

/// <summary>
/// The container fixture's readiness judgement — distinct from <c>StarRocksReadinessGateTests</c>,
/// which covers the PRODUCTION <c>StarRocksReadinessGate</c>. This one covers the TEST fixture's
/// gate, and it is unit-tested because the interesting input — a backend reporting
/// <c>Alive: true</c> with <c>AvailCapacity: 1.000 B</c> — appears only inside a startup window a
/// test cannot summon on demand. The strings below are verbatim from <c>SHOW BACKENDS</c> against
/// <c>starrocks/allin1-ubuntu</c>, the placeholder included.
/// </summary>
public sealed class StarRocksContainerReadinessTests
{
    [Theory]
    // The pre-heartbeat placeholder. This is the value that let the old Alive-only gate open early,
    // and every "backends without enough disk space" failure downstream of it.
    [InlineData("1.000 B")]
    [InlineData("0.000 B")]
    [InlineData("512.000 KB")]
    public void HasReportedDiskCapacity_ByteAndKilobyteScale_IsNotYetReported(string availCapacity) =>
        StarRocksContainerFixture.HasReportedDiskCapacity(availCapacity).Should().BeFalse();

    [Theory]
    [InlineData("907.304 GB")]
    [InlineData("1006.854 GB")]
    [InlineData("2.500 TB")]
    [InlineData("64.000 MB")]
    public void HasReportedDiskCapacity_MegabyteScaleAndAbove_IsReported(string availCapacity) =>
        StarRocksContainerFixture.HasReportedDiskCapacity(availCapacity).Should().BeTrue();

    [Fact]
    public void HasReportedDiskCapacity_MalformedValue_IsTreatedAsNotReported()
    {
        // Fail CLOSED on anything unrecognized: waiting longer costs seconds, opening the gate
        // early costs a whole suite's worth of misattributed failures.
        StarRocksContainerFixture.HasReportedDiskCapacity("").Should().BeFalse();
        StarRocksContainerFixture.HasReportedDiskCapacity("N/A").Should().BeFalse();
        StarRocksContainerFixture.HasReportedDiskCapacity("907.304").Should().BeFalse();
    }

    // ── the PRODUCTION check, which had the identical defect ──────────────────────────────────
    //
    // EngagementHealthChecker.AnyBackendReadyAsync backs both the k8s readiness probe and
    // EngagementRepository's production readiness gate. Before this, both opened on Alive=true
    // alone: on a cold start the gate opened and the first data-touching query failed with
    // "backends without enough disk space", while /health reported Healthy and k8s routed traffic
    // to a pod whose StarRocks queries could not succeed. Found by diagnosing what two rulings had
    // recorded as a test flake.

    [Theory]
    [InlineData("1.000 B")]
    [InlineData("0.000 B")]
    [InlineData("512.000 KB")]
    public void Production_HasReportedDiskCapacity_ByteAndKilobyteScale_IsNotYetReported(string availCapacity) =>
        EngagementHealthChecker.HasReportedDiskCapacity(availCapacity).Should().BeFalse();

    [Theory]
    [InlineData("907.304 GB")]
    [InlineData("2.500 TB")]
    [InlineData("64.000 MB")]
    public void Production_HasReportedDiskCapacity_MegabyteScaleAndAbove_IsReported(string availCapacity) =>
        EngagementHealthChecker.HasReportedDiskCapacity(availCapacity).Should().BeTrue();

    [Fact]
    public void Production_HasReportedDiskCapacity_MalformedValue_IsTreatedAsNotReported()
    {
        EngagementHealthChecker.HasReportedDiskCapacity("").Should().BeFalse();
        EngagementHealthChecker.HasReportedDiskCapacity("N/A").Should().BeFalse();
        EngagementHealthChecker.HasReportedDiskCapacity("907.304").Should().BeFalse();
    }

    // ── the judgement over whole rows, not just the capacity string ───────────────────────────
    //
    // Without these, deleting the capacity check from AnyBackendReady would leave every test above
    // passing — HasReportedDiskCapacity would still be correct, and still be graded, while the
    // caller stopped consulting it. That is the precise shape of defect this fix exists to remove,
    // so it must not be reintroduced one level up.

    [Fact]
    public void AnyBackendReady_AliveButStillReportingBytes_IsNotReady() =>
        EngagementHealthChecker.AnyBackendReady([("true", "1.000 B")]).Should().BeFalse(
            "a BE flips Alive to true before its first disk heartbeat; the FE rejects data queries " +
            "for the whole of that window");

    [Fact]
    public void AnyBackendReady_AliveWithRealCapacity_IsReady() =>
        EngagementHealthChecker.AnyBackendReady([("true", "907.304 GB")]).Should().BeTrue();

    [Fact]
    public void AnyBackendReady_NotAliveWithRealCapacity_IsNotReady() =>
        EngagementHealthChecker.AnyBackendReady([("false", "907.304 GB")]).Should().BeFalse();

    [Fact]
    public void AnyBackendReady_OneOfSeveralIsReady_IsReady() =>
        EngagementHealthChecker.AnyBackendReady(
            [("false", "907.304 GB"), ("true", "1.000 B"), ("true", "512.000 GB")]).Should().BeTrue(
            "one usable backend is enough — but it must be the SAME row that is both alive and has space");

    [Fact]
    public void AnyBackendReady_AliveAndSpaciousAreDifferentRows_IsNotReady() =>
        EngagementHealthChecker.AnyBackendReady(
            [("false", "907.304 GB"), ("true", "1.000 B")]).Should().BeFalse(
            "checking the two properties across the whole set rather than per row would report " +
            "ready here, with no single backend able to serve a query");

    [Fact]
    public void AnyBackendReady_NoBackendsAtAll_IsNotReady() =>
        EngagementHealthChecker.AnyBackendReady([]).Should().BeFalse();
}
