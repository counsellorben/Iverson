using FluentAssertions;
using Xunit;

namespace Iverson.StarRocks.Tests;

public class StarRocksHealthCheckerTests
{
    // MySqlException's constructors are internal to MySqlConnector with no InternalsVisibleTo
    // grant to this assembly, so an instance must be built via reflection against the exact
    // 4-arg overload — same pattern as StarRocksResiliencePipelineFactoryTests.cs.
    private static MySqlConnector.MySqlException CreateMySqlException(MySqlConnector.MySqlErrorCode errorCode, string message = "test") =>
        (MySqlConnector.MySqlException)typeof(MySqlConnector.MySqlException)
            .GetConstructor(
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                binder: null,
                types: [typeof(MySqlConnector.MySqlErrorCode), typeof(string), typeof(string), typeof(Exception)],
                modifiers: null)!
            .Invoke([errorCode, null, message, null]);

    [Theory]
    [InlineData(MySqlConnector.MySqlErrorCode.AccessDenied, StarRocksHealthStatus.AuthPending)]
    [InlineData(MySqlConnector.MySqlErrorCode.UnableToConnectToHost, StarRocksHealthStatus.Unhealthy)]
    [InlineData(MySqlConnector.MySqlErrorCode.ParseError, StarRocksHealthStatus.Unhealthy)]
    public void ClassifyConnectionException_MapsErrorCodeToStatus(
        MySqlConnector.MySqlErrorCode code, StarRocksHealthStatus expected)
    {
        var ex = CreateMySqlException(code);

        StarRocksHealthChecker.ClassifyConnectionException(ex).Should().Be(expected);
    }

    [Fact]
    public void ClassifyConnectionException_NonMySqlException_ReturnsUnhealthy()
    {
        StarRocksHealthChecker.ClassifyConnectionException(new InvalidOperationException("boom"))
            .Should().Be(StarRocksHealthStatus.Unhealthy);
    }

    [Fact]
    public void StarRocksHealthChecker_ImplementsIStarRocksHealthCheck()
    {
        typeof(StarRocksHealthChecker).Should().Implement<IStarRocksHealthCheck>();
    }
}
