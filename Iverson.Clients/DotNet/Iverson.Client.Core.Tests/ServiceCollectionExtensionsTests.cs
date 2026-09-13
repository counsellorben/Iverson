using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iverson.Client.Core.Tests;

/// <summary>
/// Closes CSR round-3 finding #4 for the .NET SDK: <c>AddIversonClient</c> must refuse to
/// attach an <c>actingUserTokenProvider</c> to a plaintext (h2c) endpoint unless the caller has
/// explicitly opted in via <c>allowInsecureChannelCallCredentials: true</c>. The acting-user
/// identity travels as raw <see cref="Grpc.Core.Metadata"/> (see
/// <see cref="ActingUserMetadata.WithActingUser"/>), not as <c>CallCredentials</c>, so
/// grpc-dotnet's own <c>UnsafeUseInsecureChannelCallCredentials</c> guard — which the round-2
/// fix relies on for the service <c>credentials</c>/<c>dataPlaneTokenProvider</c> path — cannot
/// see it. This mirrors the analogous guard already added to the Java, Python, and TypeScript
/// SDKs for the same finding.
/// </summary>
public class ServiceCollectionExtensionsTests
{
    private static Func<Task<string>> ActingUserTokenProvider => () => Task.FromResult("acting-user-token");

    [Fact]
    public void AddIversonClient_WithActingUserTokenProviderOverPlaintext_ThrowsWithoutOptIn()
    {
        var services = new ServiceCollection();

        var act = () => services.AddIversonClient(
            grpcEndpoint: "http://localhost:5000",
            actingUserTokenProvider: ActingUserTokenProvider);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*allowInsecureChannelCallCredentials*");
    }

    [Fact]
    public void AddIversonClient_WithActingUserTokenProviderOverPlaintext_SucceedsWithOptIn()
    {
        var services = new ServiceCollection();

        var act = () => services.AddIversonClient(
            grpcEndpoint: "http://localhost:5000",
            actingUserTokenProvider: ActingUserTokenProvider,
            allowInsecureChannelCallCredentials: true);

        act.Should().NotThrow();
    }

    [Fact]
    public void AddIversonClient_WithActingUserTokenProviderOverTls_DoesNotRequireOptIn()
    {
        // Happy-path regression guard: a TLS endpoint must never require the opt-in, with or
        // without an acting-user token provider.
        var services = new ServiceCollection();

        var act = () => services.AddIversonClient(
            grpcEndpoint: "https://localhost:5000",
            actingUserTokenProvider: ActingUserTokenProvider);

        act.Should().NotThrow();
    }

    [Fact]
    public void AddIversonClient_WithoutActingUserTokenProviderOverPlaintext_DoesNotRequireOptIn()
    {
        // Happy-path regression guard: no acting-user token provider at all must never trip
        // this guard, regardless of scheme.
        var services = new ServiceCollection();

        var act = () => services.AddIversonClient(grpcEndpoint: "http://localhost:5000");

        act.Should().NotThrow();
    }
}
