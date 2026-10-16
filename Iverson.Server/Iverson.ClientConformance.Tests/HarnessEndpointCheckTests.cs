using FluentAssertions;
using Iverson.LoadTest.Auth;
using Xunit;

namespace Iverson.ClientConformance.Tests;

/// <summary>
/// CSR round-11 #15: the harness's own channel is plaintext (<c>ChannelCredentials.Insecure</c>
/// with <c>UnsafeUseInsecureChannelCallCredentials</c>) and carries the service and acting-user
/// tokens, so Program.cs refuses a gRPC URL that is not loopback, https or not. The token endpoint
/// and the Authentik base URL must be https or loopback, as in LoadTest.
/// </summary>
public class HarnessEndpointCheckTests
{
    [Theory]
    [InlineData("http://localhost:8080", null)]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:9000/application/o/token/")]
    [InlineData("http://[::1]:8080", "https://idp.example.com/application/o/token/")]
    public void Check_AcceptsALoopbackGrpcUrl(string grpcUrl, string? tokenEndpoint) =>
        CredentialEndpoints.Check(grpcUrl, tokenEndpoint, grpcMayUseTls: false).Should().BeNull();

    [Theory]
    [InlineData("http://iverson-api:8080", null, "IVERSON_GRPC_URL must be a loopback address")]
    [InlineData("https://iverson-api:8080", null, "IVERSON_GRPC_URL must be a loopback address")]
    [InlineData("http://localhost:8080", "http://iverson-authentik:9000/application/o/token/", "IVERSON_TOKEN_ENDPOINT must be https:// or a loopback address")]
    [InlineData("http://localhost:8080", "localhost:9000", "IVERSON_TOKEN_ENDPOINT must be an absolute URL ending in /application/o/token/")]
    public void Check_RefusesANonLoopbackGrpcUrlOrAPlaintextNetworkTokenEndpoint(
        string grpcUrl, string? tokenEndpoint, string expected) =>
        CredentialEndpoints.Check(grpcUrl, tokenEndpoint, grpcMayUseTls: false).Should().StartWith(expected);
}
