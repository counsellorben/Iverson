using FluentAssertions;
using Grpc.Core;
using Xunit;

namespace Iverson.ClientConformance.Tests;

/// <summary>
/// CSR round 12 §4.6: the harness's channel takes its transport from IVERSON_GRPC_URL's scheme, so it
/// reaches the compose API's TLS listener on https:// and a plaintext listener on http://.
/// Grpc.Net.Client refuses, while it builds the channel, insecure credentials on an https:// address
/// and secure credentials on an http:// one, so a wrong choice throws here.
/// </summary>
public class HarnessChannelTests
{
    private static readonly CallCredentials NoOp = CallCredentials.FromInterceptor((_, _) => Task.CompletedTask);

    [Theory]
    [InlineData("https://localhost:8080")]
    [InlineData("HTTPS://localhost:8080")]
    [InlineData("http://localhost:8080")]
    public void Create_BuildsAChannelWhoseTransportMatchesTheScheme(string grpcUrl)
    {
        var act = () => HarnessChannel.Create(grpcUrl, NoOp).Dispose();

        act.Should().NotThrow();
    }
}
