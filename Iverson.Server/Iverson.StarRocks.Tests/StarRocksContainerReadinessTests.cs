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
}
