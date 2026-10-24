using FluentAssertions;
using Xunit;

namespace Iverson.ClientConformance.Tests;

/// <summary>
/// CSR round-11 #15 and round 12 §4.6: the harness's own channel carries the service and acting-user
/// tokens and is TLS for an https:// gRPC URL, plaintext h2c otherwise (<see cref="HarnessChannel"/>).
/// So Program.cs refuses a gRPC URL that is neither https nor loopback, and a token endpoint or
/// Authentik base URL that is neither https nor loopback, as LoadTest does.
/// </summary>
public class HarnessEndpointCheckTests
{
    [Theory]
    [InlineData("http://localhost:8080", null)]
    [InlineData("https://localhost:8080", null)]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:9000/application/o/token/")]
    [InlineData("http://[::1]:8080", "https://idp.example.com/application/o/token/")]
    [InlineData("https://iverson-api:8080", "https://iverson-authentik:8443/application/o/token/")]
    public void CheckEndpoints_AcceptsALoopbackOrHttpsGrpcUrl(string grpcUrl, string? tokenEndpoint) =>
        HarnessChannel.CheckEndpoints(grpcUrl, tokenEndpoint).Should().BeNull();

    [Theory]
    [InlineData("http://iverson-api:8080", null, "IVERSON_GRPC_URL must be https:// or a loopback address")]
    [InlineData("http://localhost:8080", "http://iverson-authentik:9000/application/o/token/", "IVERSON_TOKEN_ENDPOINT must be https:// or a loopback address")]
    [InlineData("http://localhost:8080", "localhost:9000", "IVERSON_TOKEN_ENDPOINT must be an absolute URL ending in /application/o/token/")]
    public void CheckEndpoints_RefusesAPlaintextNetworkGrpcUrlOrTokenEndpoint(
        string grpcUrl, string? tokenEndpoint, string expected) =>
        HarnessChannel.CheckEndpoints(grpcUrl, tokenEndpoint).Should().StartWith(expected);
}
