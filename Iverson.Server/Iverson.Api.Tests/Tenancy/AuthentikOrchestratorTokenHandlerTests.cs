using System.Net;
using FluentAssertions;
using Iverson.Api.Tenancy;
using Xunit;

namespace Iverson.Api.Tests.Tenancy;

/// <summary>
/// CSR round-10 #4 (C2): the federated Authentik credential. The handler exchanges the projected
/// service-account token for a short-lived access token, caches it until a minute before expiry,
/// and fails with a message that names the step and carries no credential.
/// </summary>
public sealed class AuthentikOrchestratorTokenHandlerTests : IDisposable
{
    private const string TokenPath = "/application/o/token/";
    private readonly string _dir = Directory.CreateTempSubdirectory("orchestrator-token-").FullName;
    private readonly FakeClock _clock = new(DateTimeOffset.Parse("2026-10-07T00:00:00Z"));

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>
    /// Mirrors AuthentikAdminClientTests' FakeHttpMessageHandler: records every request and answers
    /// the token endpoint with a numbered access token (or the configured failure).
    /// </summary>
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        public sealed record Seen(string Path, string Uri, string Body, string? Authorization);

        private int _minted;
        public List<Seen> Requests { get; } = [];
        public HttpStatusCode TokenStatus { get; init; } = HttpStatusCode.OK;
        public string? TokenBody { get; init; }
        public bool ThrowOnToken { get; init; }
        public bool TimeOutOnToken { get; init; }
        public TimeSpan Delay { get; init; }

        public int Mints => Requests.Count(r => r.Path == TokenPath);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, cancellationToken);
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests)
                Requests.Add(new Seen(request.RequestUri!.AbsolutePath, request.RequestUri.ToString(), body, request.Headers.Authorization?.ToString()));

            if (request.RequestUri.AbsolutePath != TokenPath)
                return new HttpResponseMessage(HttpStatusCode.OK);
            if (ThrowOnToken)
                throw new HttpRequestException("connection refused");
            if (TimeOutOnToken)
                throw new TaskCanceledException("The operation was canceled.", new TimeoutException("connect timed out"));
            if (TokenStatus != HttpStatusCode.OK)
                return new HttpResponseMessage(TokenStatus)
                {
                    Content = new StringContent("{\"error\":\"invalid_grant\",\"error_description\":\"The provided authorization grant is invalid\"}"),
                };
            var n = Interlocked.Increment(ref _minted);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(TokenBody?.Replace("{n}", n.ToString())
                    ?? $"{{\"access_token\":\"access-token-{n}\",\"token_type\":\"Bearer\",\"expires_in\":300,\"id_token\":\"id-token\"}}"),
            };
        }
    }

    private string WriteAssertion(string content)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, content);
        return path;
    }

    private HttpClient Client(string assertionPath, FakeHttpMessageHandler inner) =>
        new(new AuthentikOrchestratorTokenHandler(assertionPath, "iverson-admin-orchestrator", _clock) { InnerHandler = inner })
        {
            BaseAddress = new Uri("https://iverson-authentik:8443"),
        };

    private HttpClient Client(string assertionPath, FakeHttpMessageHandler inner, AuthentikOrchestratorTokenCache cache) =>
        new(new AuthentikOrchestratorTokenHandler(assertionPath, "iverson-admin-orchestrator", _clock, cache) { InnerHandler = inner })
        {
            BaseAddress = new Uri("https://iverson-authentik:8443"),
        };

    [Fact]
    public async Task FirstCall_ExchangesTheAssertion_ThenSendsTheAccessToken()
    {
        var inner = new FakeHttpMessageHandler();
        using var client = Client(WriteAssertion("projected-sa-token\n"), inner);

        await client.GetAsync("/api/v3/core/users/");

        inner.Requests.Should().HaveCount(2);
        var exchange = inner.Requests[0];
        exchange.Uri.Should().Be("https://iverson-authentik:8443/application/o/token/");
        exchange.Body.Should().Contain("grant_type=client_credentials")
            .And.Contain("client_id=iverson-admin-orchestrator")
            .And.Contain("client_assertion_type=urn%3Aietf%3Aparams%3Aoauth%3Aclient-assertion-type%3Ajwt-bearer")
            .And.Contain("client_assertion=projected-sa-token&")
            .And.Contain("scope=goauthentik.io%2Fapi");
        inner.Requests[1].Path.Should().Be("/api/v3/core/users/");
        inner.Requests[1].Authorization.Should().Be("Bearer access-token-1");
    }

    [Fact]
    public async Task CachedToken_IsReused_UntilTheRefreshMargin()
    {
        var inner = new FakeHttpMessageHandler();
        using var client = Client(WriteAssertion("projected-sa-token"), inner);

        await client.GetAsync("/a");
        _clock.Now += TimeSpan.FromSeconds(239); // expires_in 300, margin 60: still fresh
        await client.GetAsync("/b");
        inner.Mints.Should().Be(1);

        _clock.Now += TimeSpan.FromSeconds(1);   // inside the margin
        await client.GetAsync("/c");
        inner.Mints.Should().Be(2);
        inner.Requests[^1].Authorization.Should().Be("Bearer access-token-2");
    }

    [Fact]
    public async Task EveryMint_ReReadsTheAssertionFile()
    {
        var inner = new FakeHttpMessageHandler();
        var path = WriteAssertion("first-assertion");
        using var client = Client(path, inner);

        await client.GetAsync("/a");
        File.WriteAllText(path, "rotated-assertion");
        _clock.Now += TimeSpan.FromSeconds(300);
        await client.GetAsync("/b");

        inner.Requests.Where(r => r.Path == TokenPath).Select(r => r.Body)
            .Should().SatisfyRespectively(
                first => first.Should().Contain("client_assertion=first-assertion"),
                second => second.Should().Contain("client_assertion=rotated-assertion"));
    }

    [Fact]
    public async Task ConcurrentCalls_MintOnce()
    {
        var inner = new FakeHttpMessageHandler { Delay = TimeSpan.FromMilliseconds(50) };
        using var client = Client(WriteAssertion("projected-sa-token"), inner);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => client.GetAsync("/a")));

        inner.Mints.Should().Be(1);
    }

    [Fact]
    public async Task HandlersSharingOneCache_MintOnce()
    {
        // IHttpClientFactory rebuilds the handler chain every couple of minutes; the shared cache
        // carries the token across the rotation.
        var inner = new FakeHttpMessageHandler();
        var path = WriteAssertion("projected-sa-token");
        var cache = new AuthentikOrchestratorTokenCache();
        using var first = Client(path, inner, cache);
        using var second = Client(path, inner, cache);

        await first.GetAsync("/a");
        await second.GetAsync("/b");

        inner.Mints.Should().Be(1);
        inner.Requests[^1].Authorization.Should().Be("Bearer access-token-1");
    }

    public static TheoryData<string> UnusableExpiresIn() => new()
    {
        "{\"access_token\":\"access-token-{n}\",\"expires_in\":\"300\"}",
        "{\"access_token\":\"access-token-{n}\"}",
    };

    [Theory]
    [MemberData(nameof(UnusableExpiresIn))]
    public async Task UnusableExpiresIn_UsesTheTokenOnce_WithoutCaching(string tokenBody)
    {
        var inner = new FakeHttpMessageHandler { TokenBody = tokenBody };
        using var client = Client(WriteAssertion("projected-sa-token"), inner);

        await client.GetAsync("/a");
        await client.GetAsync("/b");

        inner.Mints.Should().Be(2);
        inner.Requests.Where(r => r.Path != TokenPath).Select(r => r.Authorization)
            .Should().Equal("Bearer access-token-1", "Bearer access-token-2");
    }

    [Fact]
    public async Task CallerCancellation_SurfacesAsCancellation()
    {
        var inner = new FakeHttpMessageHandler { Delay = TimeSpan.FromSeconds(30) };
        using var client = Client(WriteAssertion("projected-sa-token"), inner);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var thrown = await FluentActions.Awaiting(() => client.GetAsync("/a", cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        thrown.Which.Should().NotBeAssignableTo<InvalidOperationException>();
    }

    public static TheoryData<string, string> Failures() => new()
    {
        { "missing-file", "could not be read" },
        { "empty-file", "is empty" },
        { "http-400", "HTTP 400 invalid_grant" },
        { "network", "Authentik token exchange failed: connection refused" },
        { "timeout", "Authentik token exchange failed" },
        { "no-access-token", "returned no access_token" },
        { "numeric-access-token", "returned no access_token" },
        { "non-object-body", "returned no access_token" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Failure_NamesTheStep_AndCarriesNoCredential(string failure, string expected)
    {
        const string assertion = "assertion-that-must-not-leak";
        var path = failure switch
        {
            "missing-file" => Path.Combine(_dir, "absent"),
            "empty-file" => WriteAssertion("  \n"),
            _ => WriteAssertion(assertion),
        };
        var inner = failure switch
        {
            "http-400" => new FakeHttpMessageHandler { TokenStatus = HttpStatusCode.BadRequest },
            "network" => new FakeHttpMessageHandler { ThrowOnToken = true },
            "timeout" => new FakeHttpMessageHandler { TimeOutOnToken = true },
            "numeric-access-token" => new FakeHttpMessageHandler { TokenBody = "{\"access_token\":12345,\"expires_in\":300,\"id_token\":\"id-token\"}" },
            "non-object-body" => new FakeHttpMessageHandler { TokenBody = "[\"id-token\"]" },
            "no-access-token" => new FakeHttpMessageHandler { TokenBody = "{\"token_type\":\"Bearer\",\"id_token\":\"id-token\"}" },
            _ => new FakeHttpMessageHandler(),
        };
        using var client = Client(path, inner);

        var thrown = await FluentActions.Awaiting(() => client.GetAsync("/a")).Should().ThrowAsync<InvalidOperationException>();

        thrown.Which.Message.Should().Contain(expected)
            .And.NotContain(assertion)
            .And.NotContain("id-token")
            .And.NotContain("access-token");
        inner.Requests.Should().NotContain(r => r.Path != TokenPath, "no admin call is sent without a token");
    }
}
