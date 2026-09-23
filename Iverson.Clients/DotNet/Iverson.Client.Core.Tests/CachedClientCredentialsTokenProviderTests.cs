using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Xunit;

namespace Iverson.Client.Core.Tests;

public class CachedClientCredentialsTokenProviderTests
{
    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task GetTokenAsync_OnRedirectFromTokenEndpoint_DoesNotFollowAndThrows()
    {
        var redirectTargetHit = false;
        var redirectPort = GetFreePort();
        var tokenPort = GetFreePort();

        using var redirectListener = new HttpListener();
        redirectListener.Prefixes.Add($"http://127.0.0.1:{redirectPort}/");
        redirectListener.Start();
        var redirectTask = Task.Run(async () =>
        {
            var ctx = await redirectListener.GetContextAsync();
            redirectTargetHit = true;
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
        });

        using var tokenListener = new HttpListener();
        tokenListener.Prefixes.Add($"http://127.0.0.1:{tokenPort}/");
        tokenListener.Start();
        var tokenTask = Task.Run(async () =>
        {
            var ctx = await tokenListener.GetContextAsync();
            ctx.Response.StatusCode = 307;
            ctx.Response.Headers.Add("Location", $"http://127.0.0.1:{redirectPort}/");
            ctx.Response.Close();
        });

        var credentials = new IversonClientCredentials(
            "client-id", "client-secret", $"http://127.0.0.1:{tokenPort}/token");
        var provider = new CachedClientCredentialsTokenProvider(credentials);

        var act = () => provider.GetTokenAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
        redirectTargetHit.Should().BeFalse();

        redirectListener.Stop();
        tokenListener.Stop();
    }
}
