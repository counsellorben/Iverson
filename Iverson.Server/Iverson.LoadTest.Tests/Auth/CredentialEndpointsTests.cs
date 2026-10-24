using FluentAssertions;
using Iverson.LoadTest.Auth;
using Xunit;

namespace Iverson.LoadTest.Tests.Auth;

// CSR round-11 #15: LoadTest sends credentials to its gRPC URL only when it is loopback or https,
// and to the token endpoint and the Authentik base URL only when they are https or loopback; with a
// non-loopback https gRPC URL the token endpoint must be https.
public class CredentialEndpointsTests
{
    [Theory]
    [InlineData("http://localhost:8080", null)]
    [InlineData("http://LOCALHOST:8080", "http://localhost:9000/application/o/token/")]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:9000/application/o/token/")]
    [InlineData("http://[::1]:8080", "http://[::1]:9000/application/o/token/")]
    [InlineData("https://iverson-api:8080", "https://iverson-authentik:8443/application/o/token/")]
    [InlineData("http://localhost:8080", "https://idp.example.com/application/o/token/")]
    public void Check_AcceptsLoopbackOrHttpsEndpoints(string grpcUrl, string? tokenEndpoint) =>
        CredentialEndpoints.Check(grpcUrl, tokenEndpoint, grpcMayUseTls: true).Should().BeNull();

    [Theory]
    [InlineData("http://iverson-api:8080", null, "IVERSON_GRPC_URL must be https:// or a loopback address")]
    [InlineData("http://0.0.0.0:8080", null, "IVERSON_GRPC_URL must be https:// or a loopback address")]
    [InlineData("not a url", null, "IVERSON_GRPC_URL must be https:// or a loopback address")]
    [InlineData("http://localhost:8080", "http://iverson-authentik:9000/application/o/token/", "IVERSON_TOKEN_ENDPOINT must be https:// or a loopback address")]
    [InlineData("https://iverson-api:8080", "http://localhost:9000/application/o/token/", "IVERSON_TOKEN_ENDPOINT must be https:// when IVERSON_GRPC_URL is not a loopback address")]
    [InlineData("http://localhost:8080", "https://iverson-authentik:8443/token", "IVERSON_TOKEN_ENDPOINT must be an absolute URL ending in /application/o/token/")]
    [InlineData("http://localhost:8080", "localhost:9000", "IVERSON_TOKEN_ENDPOINT must be an absolute URL ending in /application/o/token/")]
    // The shape is checked first: a malformed token endpoint is named even when the gRPC URL is also refused.
    [InlineData("http://iverson-api:8080", "not a url", "IVERSON_TOKEN_ENDPOINT must be an absolute URL ending in /application/o/token/")]
    public void Check_RefusesAPlaintextNetworkEndpointOrAMalformedTokenEndpoint(
        string grpcUrl, string? tokenEndpoint, string expected) =>
        CredentialEndpoints.Check(grpcUrl, tokenEndpoint, grpcMayUseTls: true).Should().StartWith(expected);

    [Theory]
    [InlineData("http://localhost:8080", true)]
    [InlineData("http://127.0.0.1:8080", true)]
    [InlineData("http://[::1]:8080", true)]
    [InlineData("https://iverson-api:8080", false)]
    [InlineData("http://iverson-api:8080", false)]
    public void IsLoopback_IsTrueOnlyForALoopbackHost(string url, bool expected) =>
        CredentialEndpoints.IsLoopback(url).Should().Be(expected);

    [Theory]
    // CSR round 12 §4.6: compose publishes Authentik only through authentik-tls-proxy on 9443.
    [InlineData(null, "https://localhost:9443")]
    [InlineData("https://iverson-authentik:8443/application/o/token/", "https://iverson-authentik:8443")]
    public void AuthentikBaseUrl_IsTheTokenEndpointWithoutItsPath(string? tokenEndpoint, string expected) =>
        CredentialEndpoints.AuthentikBaseUrl(tokenEndpoint).Should().Be(expected);
}
