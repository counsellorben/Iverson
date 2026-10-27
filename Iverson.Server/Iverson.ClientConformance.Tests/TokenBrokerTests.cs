using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Iverson.Client.Core;
using Xunit;

namespace Iverson.ClientConformance.Tests;

// CSR round 12 §5.1: TokenBroker's client-credentials token request does not follow a redirect, so the
// client secret goes only to the configured token endpoint; a 3xx is a failed request naming its Location.
public class TokenBrokerTests
{
    // A loopback token endpoint that answers every request with a 307 to another path on itself and counts the
    // requests, so a test sees whether the client's real handler followed the redirect; a scripted
    // HttpMessageHandler never follows one.
    private sealed class RedirectingEndpoint : IDisposable
    {
        private readonly HttpListener _listener = new();
        private int _requests;

        public RedirectingEndpoint()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://127.0.0.1:{port}/application/o/token/";
            Location = $"http://127.0.0.1:{port}/elsewhere/";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public string Url { get; }
        public string Location { get; }
        public int Requests => Volatile.Read(ref _requests);

        private async Task ServeAsync()
        {
            while (true)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { return; }
                Interlocked.Increment(ref _requests);
                context.Response.StatusCode = 307;
                context.Response.RedirectLocation = Location;
                context.Response.Close();
            }
        }

        public void Dispose() => _listener.Close();
    }

    private sealed class Redirect(string location) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            response.Headers.Location = new Uri(location);
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task MintClientCredentialsToken_DoesNotFollowARedirect()
    {
        using var endpoint = new RedirectingEndpoint();

        var act = () => TokenBroker.MintClientCredentialsTokenAsync(
            new IversonClientCredentials("id", "s3cret", endpoint.Url), CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage($"*redirected to {endpoint.Location}*");
        endpoint.Requests.Should().Be(1);
    }

    [Fact]
    public async Task MintClientCredentialsToken_WithAHandler_SendsThroughIt_AndNamesTheRedirectsLocation()
    {
        var handler = new Redirect("https://example.invalid/elsewhere/");

        var act = () => TokenBroker.MintClientCredentialsTokenAsync(
            new IversonClientCredentials("id", "s3cret", "https://localhost:9443/application/o/token/"),
            CancellationToken.None, handler);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*redirected to https://example.invalid/elsewhere/*");
        handler.Requests.Should().Be(1);
    }
}
