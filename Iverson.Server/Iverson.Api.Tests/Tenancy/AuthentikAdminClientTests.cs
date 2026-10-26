using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using FluentAssertions;
using Iverson.Api.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Tenancy;

public sealed class AuthentikAdminClientTests
{
    /// <summary>
    /// Mirrors EmbeddingServiceTests' FakeHttpMessageHandler, extended to return a queue of
    /// responses in order (AuthentikAdminClient often issues more than one HTTP call per logical
    /// operation, e.g. resolve-group-pk then create-user then recovery-link). If fewer responses
    /// are supplied than requests made, the last response is reused for any remaining requests.
    /// </summary>
    private sealed class FakeHttpMessageHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _index;

        public List<HttpRequestMessage> Requests     { get; } = [];
        public List<string?>            RequestBodies { get; } = [];

        /// <summary>
        /// Content-Length per request that had a body, captured AT SEND TIME. HttpClient disposes
        /// the request content once the call completes, so reading Headers afterwards throws
        /// ObjectDisposedException — a test asserting on it later fails for the wrong reason.
        /// </summary>
        public List<(string Uri, long? ContentLength, bool? Chunked)> BodyFraming { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            Requests.Add(request);

            // Framing MUST be captured before the body is read: ReadAsStringAsync buffers the
            // content, which populates Content-Length after the fact. Reading first made a
            // chunked, length-less JsonContent look length-delimited and the test pass vacuously.
            if (request.Content is not null)
                BodyFraming.Add((
                    request.RequestUri!.AbsolutePath,
                    request.Content.Headers.ContentLength,
                    request.Headers.TransferEncodingChunked));

            RequestBodies.Add(request.Content is not null
                ? await request.Content.ReadAsStringAsync(ct)
                : null);

            var response = responses[Math.Min(_index, responses.Length - 1)];
            _index++;
            return response;
        }
    }

    private static IdpAdminClient CreateClient(FakeHttpMessageHandler handler, out FakeHttpMessageHandler exposedHandler) =>
        CreateClient(handler, out exposedHandler, out _);

    private static IdpAdminClient CreateClient(
        FakeHttpMessageHandler handler,
        out FakeHttpMessageHandler exposedHandler,
        out ILogger<IdpAdminClient> logger,
        IConfiguration? configuration = null)
    {
        exposedHandler = handler;
        var factory = Substitute.For<IHttpClientFactory>();
        factory
            .CreateClient(IdpAdminClient.HttpClientName)
            .Returns(_ => new HttpClient(handler) { BaseAddress = new Uri("http://authentik.local") });
        logger = Substitute.For<ILogger<IdpAdminClient>>();
        return new IdpAdminClient(factory, logger, configuration ?? new ConfigurationBuilder().Build());
    }

    private static IdpAdminClient CreateClient(FakeHttpMessageHandler handler, IConfiguration configuration, out FakeHttpMessageHandler exposedHandler) =>
        CreateClient(handler, out exposedHandler, out _, configuration);

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage[] OnboardingResponses(string link) =>
    [
        JsonResponse(HttpStatusCode.OK,
            """{"pagination":{"next":0},"results":[{"pk":"11111111-1111-1111-1111-111111111111","name":"tenant-admins"}]}"""),
        JsonResponse(HttpStatusCode.Created, """{"pk":42,"username":"new-user","email":"new-user@example.invalid"}"""),
        JsonResponse(HttpStatusCode.OK, $$"""{"link":{{JsonSerializer.Serialize(link)}}}"""),
    ];

    [Fact]
    public async Task CreateUserAsync_AsksForAFifteenMinuteRecoveryLink()
    {
        var sut = CreateClient(new FakeHttpMessageHandler(OnboardingResponses("http://authentik.local/if/flow/iverson-recovery/?flow_token=abc123")), out var handler);

        await sut.CreateUserAsync("new-user", "new-user@example.invalid", "tenant-a", ["tenant-admins"]);

        using var body = JsonDocument.Parse(handler.RequestBodies[2]!);
        body.RootElement.GetProperty("token_duration").GetString().Should().Be("minutes=15");
    }

    [Theory]
    [InlineData("https://authentik.iverson.example.com", "https://authentik.iverson.example.com/if/flow/iverson-recovery/?flow_token=abc123")]
    [InlineData("http://localhost:9000", "http://localhost:9000/if/flow/iverson-recovery/?flow_token=abc123")]
    public async Task CreateUserAsync_ReturnsTheLinkOnThePublicAuthentikHost(string publicBaseUrl, string expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Authentik:PublicBaseUrl"] = publicBaseUrl }).Build();
        var sut = CreateClient(new FakeHttpMessageHandler(OnboardingResponses("https://iverson-authentik:8443/if/flow/iverson-recovery/?flow_token=abc123")),
            configuration, out _);

        var result = await sut.CreateUserAsync("new-user", "new-user@example.invalid", "tenant-a", ["tenant-admins"]);

        result.RecoveryLink.Should().Be(expected);
    }

    [Theory]
    [InlineData("https://authentik.iverson.example.com", "/if/flow/iverson-recovery/?flow_token=abc123")]
    [InlineData("", "https://iverson-authentik:8443/if/flow/iverson-recovery/?flow_token=abc123")]
    [InlineData("https://authentik.iverson.example.com", "http:/iverson-authentik/if/flow/iverson-recovery/?flow_token=abc123")]
    [InlineData("https://authentik.iverson.example.com", @"http:\\iverson-authentik/if/flow/iverson-recovery/?flow_token=abc123")]
    public async Task CreateUserAsync_LeavesTheLinkUnchangedWhenItCannotOrNeedNotBeRewritten(string publicBaseUrl, string link)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Authentik:PublicBaseUrl"] = publicBaseUrl }).Build();
        var sut = CreateClient(new FakeHttpMessageHandler(OnboardingResponses(link)), configuration, out _);

        var result = await sut.CreateUserAsync("new-user", "new-user@example.invalid", "tenant-a", ["tenant-admins"]);

        result.RecoveryLink.Should().Be(link);
    }

    [Theory]
    [InlineData("https://iverson-authentik:8443/if/flow/iverson-recovery/?flow_token=a%2f%41%3d", "https://authentik.iverson.example.com/if/flow/iverson-recovery/?flow_token=a%2f%41%3d")]
    [InlineData("https://iverson-authentik:8443/if/flow/iverson-recovery/?flow_token=a%2Fb%3D", "https://authentik.iverson.example.com/if/flow/iverson-recovery/?flow_token=a%2Fb%3D")]
    [InlineData("https://iverson-authentik:8443", "https://authentik.iverson.example.com/")]
    public async Task CreateUserAsync_KeepsThePathAndQueryByteForByte(string link, string expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Authentik:PublicBaseUrl"] = "https://authentik.iverson.example.com" }).Build();
        var sut = CreateClient(new FakeHttpMessageHandler(OnboardingResponses(link)), configuration, out _);

        var result = await sut.CreateUserAsync("new-user", "new-user@example.invalid", "tenant-a", ["tenant-admins"]);

        result.RecoveryLink.Should().Be(expected);
    }

    [Fact]
    public async Task CreateUserAsync_ResolvesGroupThenCreatesUserThenTriggersRecovery()
    {
        var groupLookup = JsonResponse(HttpStatusCode.OK,
            """{"pagination":{"next":0},"results":[{"pk":"11111111-1111-1111-1111-111111111111","name":"tenant-admins"}]}""");
        var createUser = JsonResponse(HttpStatusCode.Created,
            """{"pk":42,"username":"new-user","email":"new-user@example.invalid"}""");
        var recovery = JsonResponse(HttpStatusCode.OK, """{"link":"http://authentik.local/if/flow/recovery/abc123"}""");

        var sut = CreateClient(new FakeHttpMessageHandler(groupLookup, createUser, recovery), out var handler);

        var result = await sut.CreateUserAsync(
            "new-user",
            "new-user@example.invalid",
            "tenant-a",
            ["tenant-admins"]);

        result.UserId.Should().Be("42");
        result.RecoveryLink.Should().Be("http://authentik.local/if/flow/recovery/abc123");

        handler.Requests.Should().HaveCount(3);
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].RequestUri!.PathAndQuery.Should().Be("/api/v3/core/groups/?name=tenant-admins&include_users=false");

        handler.Requests[1].Method.Should().Be(HttpMethod.Post);
        handler.Requests[1].RequestUri!.AbsolutePath.Should().Be("/api/v3/core/users/");
        using (var body = JsonDocument.Parse(handler.RequestBodies[1]!))
        {
            body.RootElement.GetProperty("username").GetString().Should().Be("new-user");
            body.RootElement.GetProperty("email").GetString().Should().Be("new-user@example.invalid");
            body.RootElement.GetProperty("is_active").GetBoolean().Should().BeTrue();
            body.RootElement.GetProperty("attributes").GetProperty("tenant_id").GetString().Should().Be("tenant-a");
            body.RootElement.GetProperty("groups")[0].GetString().Should().Be("11111111-1111-1111-1111-111111111111");
        }

        handler.Requests[2].Method.Should().Be(HttpMethod.Post);
        handler.Requests[2].RequestUri!.AbsolutePath.Should().Be("/api/v3/core/users/42/recovery/");
    }

    /// <summary>
    /// CSR finding #4 regression coverage: CreateUserAsync must never call set_password. This
    /// asserts on the full sequence of paths hit — including that recovery/ is the only POST
    /// after user-creation — so a future change that reintroduces set_password (even alongside
    /// a recovery call) fails this test.
    /// </summary>
    [Fact]
    public async Task CreateUserAsync_NeverCallsSetPassword()
    {
        var groupLookup = JsonResponse(HttpStatusCode.OK,
            """{"pagination":{"next":0},"results":[{"pk":"11111111-1111-1111-1111-111111111111","name":"tenant-admins"}]}""");
        var createUser = JsonResponse(HttpStatusCode.Created,
            """{"pk":42,"username":"new-user","email":"new-user@example.invalid"}""");
        var recovery = JsonResponse(HttpStatusCode.OK, """{"link":"http://authentik.local/if/flow/recovery/abc123"}""");

        var sut = CreateClient(new FakeHttpMessageHandler(groupLookup, createUser, recovery), out var handler);

        await sut.CreateUserAsync("new-user", "new-user@example.invalid", "tenant-a", ["tenant-admins"]);

        handler.Requests.Should().NotContain(r => r.RequestUri!.AbsolutePath.Contains("set_password"));
        handler.RequestBodies.Should().NotContain(b => b != null && b.Contains("password"));
    }

    [Fact]
    public async Task CreateUserAsync_NoGroups_SkipsGroupResolution()
    {
        var createUser = JsonResponse(HttpStatusCode.Created,
            """{"pk":7,"username":"u","email":"u@example.invalid"}""");
        var recovery = JsonResponse(HttpStatusCode.OK, """{"link":"http://authentik.local/if/flow/recovery/xyz"}""");

        var sut = CreateClient(new FakeHttpMessageHandler(createUser, recovery), out var handler);

        var result = await sut.CreateUserAsync("u", "u@example.invalid", "tenant-b", []);

        result.UserId.Should().Be("7");
        result.RecoveryLink.Should().Be("http://authentik.local/if/flow/recovery/xyz");
        handler.Requests.Should().HaveCount(2);
        using var body = JsonDocument.Parse(handler.RequestBodies[0]!);
        body.RootElement.GetProperty("groups").GetArrayLength().Should().Be(0);
    }

    /// <summary>
    /// CSR round-3 finding #3: the recovery link is a bearer credential — whoever holds it can set
    /// the account's password and reach an authenticated session — so it must never reach a log
    /// sink. This asserts on the rendered log state rather than on the message template, because a
    /// structured logger renders {RecoveryLink} into the state; checking the template alone would
    /// pass even if the link were passed as an argument.
    /// </summary>
    [Fact]
    public async Task CreateUserAsync_NeverLogsTheRecoveryLinkItself()
    {
        const string secretLink = "http://authentik.local/if/flow/iverson-recovery/?flow_token=SUPER-SECRET-TOKEN";
        var createUser = JsonResponse(HttpStatusCode.Created, """{"pk":7,"username":"u","email":"u@example.invalid"}""");
        var recovery = JsonResponse(HttpStatusCode.OK, $$"""{"link":"{{secretLink}}"}""");

        var sut = CreateClient(new FakeHttpMessageHandler(createUser, recovery), out _, out var logger);

        var result = await sut.CreateUserAsync("u", "u@example.invalid", "tenant-b", []);

        result.RecoveryLink.Should().Be(secretLink, "the caller still gets the link via the return value");

        logger.DidNotReceive().Log(
            Arg.Any<LogLevel>(),
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state.ToString()!.Contains("SUPER-SECRET-TOKEN")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());

        // ...but the outcome is still recorded, keyed by user id only.
        logger.Received(1).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state.ToString()!.Contains("recovery link created for new user 7")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task CreateUserAsync_RecoveryResponseMissingLink_LogsWarningButStillReturnsUserId()
    {
        var createUser = JsonResponse(HttpStatusCode.Created, """{"pk":7,"username":"u","email":"u@example.invalid"}""");
        var recovery = JsonResponse(HttpStatusCode.OK, "{}"); // no "link" property

        var sut = CreateClient(new FakeHttpMessageHandler(createUser, recovery), out _, out var logger);

        var result = await sut.CreateUserAsync("u", "u@example.invalid", "tenant-b", []);

        result.UserId.Should().Be("7");
        result.RecoveryLink.Should().BeNull();
        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("recovery link response")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    // CSR round 12 §2.1: the attributes filter, {"tenant_id": t} as JSON, rides on the request.
    private static string? AttributesFilterOf(HttpRequestMessage request) =>
        HttpUtility.ParseQueryString(request.RequestUri!.Query)["attributes"];

    [Fact]
    public async Task ListUsersByTenantAsync_AsksAuthentikForTheTenantsUsersOnly()
    {
        var page = JsonResponse(HttpStatusCode.OK, """
            {
              "pagination": {"next": 0},
              "results": [
                {"pk": 1, "uid": "uid-alice", "username": "alice", "email": "alice@example.invalid", "attributes": {"tenant_id": "tenant-a"}}
              ]
            }
            """);

        var sut = CreateClient(new FakeHttpMessageHandler(page), out var handler);

        var users = (await sut.ListUsersByTenantAsync("tenant-a")).ToList();

        users.Should().ContainSingle().Which.Should().BeEquivalentTo(new IdpUser("1", "alice", "alice@example.invalid", "uid-alice"));
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/api/v3/core/users/");
        AttributesFilterOf(handler.Requests[0]).Should().Be("""{"tenant_id":"tenant-a"}""");
    }

    // Authentik answers with the unfiltered list when filtering raises a ValueError, so the client
    // keeps its own check: a user of another tenant, or of none, is dropped even when the server
    // returns it.
    [Fact]
    public async Task ListUsersByTenantAsync_DropsAUserOfAnotherTenant_EvenWhenAuthentikReturnsIt()
    {
        var page = JsonResponse(HttpStatusCode.OK, """
            {
              "pagination": {"next": 0},
              "results": [
                {"pk": 1, "uid": "uid-alice", "username": "alice", "email": "alice@example.invalid", "attributes": {"tenant_id": "tenant-a"}},
                {"pk": 2, "uid": "uid-bob", "username": "bob", "email": "bob@example.invalid", "attributes": {"tenant_id": "tenant-b"}},
                {"pk": 3, "uid": "uid-carol", "username": "carol", "email": "carol@example.invalid", "attributes": {}}
              ]
            }
            """);

        var sut = CreateClient(new FakeHttpMessageHandler(page), out _);

        var users = (await sut.ListUsersByTenantAsync("tenant-a")).ToList();

        users.Select(u => u.Username).Should().Equal("alice");
    }

    [Fact]
    public async Task ListUsersByTenantAsync_FollowsPagination_AcrossMultiplePages()
    {
        var page1 = JsonResponse(HttpStatusCode.OK, """
            {
              "pagination": {"next": 2},
              "results": [
                {"pk": 1, "uid": "uid-alice", "username": "alice", "email": "alice@example.invalid", "attributes": {"tenant_id": "tenant-a"}}
              ]
            }
            """);
        var page2 = JsonResponse(HttpStatusCode.OK, """
            {
              "pagination": {"next": 0},
              "results": [
                {"pk": 3, "uid": "uid-carol", "username": "carol", "email": "carol@example.invalid", "attributes": {"tenant_id": "tenant-a"}}
              ]
            }
            """);

        var sut = CreateClient(new FakeHttpMessageHandler(page1, page2), out var handler);

        var users = (await sut.ListUsersByTenantAsync("tenant-a")).ToList();

        users.Should().HaveCount(2);
        users.Select(u => u.Username).Should().BeEquivalentTo(["alice", "carol"]);
        handler.Requests.Should().HaveCount(2);
        // Every page carries the filter: each page is a fresh query, and one without the filter
        // would page through the whole directory.
        handler.Requests.Should().AllSatisfy(r => AttributesFilterOf(r).Should().Be("""{"tenant_id":"tenant-a"}"""));
        HttpUtility.ParseQueryString(handler.Requests[1].RequestUri!.Query)["page"].Should().Be("2");
    }

    /// <summary>
    /// Regression coverage for the fail-open finding: a response missing the "pagination"
    /// envelope entirely (e.g. a proxy error page, an API version drift, a malformed
    /// response) used to be treated the same as an honest "no more pages" signal, silently
    /// truncating the tenant's user list. It must now raise instead.
    /// </summary>
    [Fact]
    public async Task ListUsersByTenantAsync_MissingPaginationEnvelope_Throws()
    {
        var page = JsonResponse(HttpStatusCode.OK, """
            {
              "results": [
                {"pk": 1, "uid": "uid-alice", "username": "alice", "email": "alice@example.invalid", "attributes": {"tenant_id": "tenant-a"}}
              ]
            }
            """);
        var sut = CreateClient(new FakeHttpMessageHandler(page), out _);

        await sut.Invoking(s => s.ListUsersByTenantAsync("tenant-a"))
                 .Should().ThrowAsync<InvalidOperationException>()
                 .WithMessage("*pagination*");
    }

    [Fact]
    public async Task ListUsersByTenantAsync_PaginationMissingNext_Throws()
    {
        var page = JsonResponse(HttpStatusCode.OK, """
            {
              "pagination": {"previous": 0, "count": 1},
              "results": [
                {"pk": 1, "uid": "uid-alice", "username": "alice", "email": "alice@example.invalid", "attributes": {"tenant_id": "tenant-a"}}
              ]
            }
            """);
        var sut = CreateClient(new FakeHttpMessageHandler(page), out _);

        await sut.Invoking(s => s.ListUsersByTenantAsync("tenant-a"))
                 .Should().ThrowAsync<InvalidOperationException>()
                 .WithMessage("*next*");
    }

    [Fact]
    public async Task ListUsersByTenantAsync_PaginationNextIsNotANumber_Throws()
    {
        var page = JsonResponse(HttpStatusCode.OK, """
            {
              "pagination": {"next": null, "previous": 0, "count": 1},
              "results": [
                {"pk": 1, "uid": "uid-alice", "username": "alice", "email": "alice@example.invalid", "attributes": {"tenant_id": "tenant-a"}}
              ]
            }
            """);
        var sut = CreateClient(new FakeHttpMessageHandler(page), out _);

        await sut.Invoking(s => s.ListUsersByTenantAsync("tenant-a"))
                 .Should().ThrowAsync<InvalidOperationException>()
                 .WithMessage("*next*");
    }

    [Fact]
    public async Task ListUsersByTenantAsync_PaginationNextIsNegative_Throws()
    {
        var page = JsonResponse(HttpStatusCode.OK, """
            {
              "pagination": {"next": -1, "previous": 0, "count": 1},
              "results": [
                {"pk": 1, "uid": "uid-alice", "username": "alice", "email": "alice@example.invalid", "attributes": {"tenant_id": "tenant-a"}}
              ]
            }
            """);
        var sut = CreateClient(new FakeHttpMessageHandler(page), out _);

        await sut.Invoking(s => s.ListUsersByTenantAsync("tenant-a"))
                 .Should().ThrowAsync<InvalidOperationException>()
                 .WithMessage("*negative*");
    }

    /// <summary>
    /// The offboarding path (DeactivateAllUsersInTenantAsync) lists users through the exact
    /// same method, so an unrecognised envelope must abort offboarding too rather than
    /// deactivating a truncated, silently-incomplete set of users.
    /// </summary>
    [Fact]
    public async Task DeactivateAllUsersInTenantAsync_UnrecognisedPaginationEnvelope_ThrowsAndDeactivatesNoOne()
    {
        var page = JsonResponse(HttpStatusCode.OK, """
            {
              "results": [
                {"pk": 1, "uid": "uid-alice", "username": "alice", "email": "alice@example.invalid", "attributes": {"tenant_id": "tenant-a"}}
              ]
            }
            """);
        var sut = CreateClient(new FakeHttpMessageHandler(page), out var handler);

        await sut.Invoking(s => s.DeactivateAllUsersInTenantAsync("tenant-a"))
                 .Should().ThrowAsync<InvalidOperationException>();

        // Only the list call happened; no PATCH was ever attempted against a user pulled
        // from the unrecognised, possibly-truncated page.
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
    }

    // ── CSR round 12 §2.1: one user by pk ──────────────────────────────────────────────────────

    private const string UserTwelve = """
        {"pk": 12, "uid": "uid-twelve", "username": "twelve", "email": "twelve@example.invalid", "is_active": true,
         "groups": ["22222222-2222-2222-2222-222222222222"], "attributes": {"tenant_id": "tenant-a"}}
        """;

    [Fact]
    public async Task GetUserAsync_ReadsTheUserByPk()
    {
        var sut = CreateClient(new FakeHttpMessageHandler(JsonResponse(HttpStatusCode.OK, UserTwelve)), out var handler);

        var user = await sut.GetUserAsync("12");

        user.Should().BeEquivalentTo(
            new IdpUserRecord("12", "twelve", "twelve@example.invalid", "uid-twelve", true,
                ["22222222-2222-2222-2222-222222222222"], "tenant-a"),
            options => options.ComparingByMembers<IdpUserRecord>());
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].RequestUri!.PathAndQuery.Should().Be("/api/v3/core/users/12/");
    }

    [Fact]
    public async Task GetUserAsync_UserWithNoTenantId_HasANullTenant()
    {
        var sut = CreateClient(new FakeHttpMessageHandler(JsonResponse(HttpStatusCode.OK,
            """{"pk": 12, "uid": "uid-twelve", "username": "twelve", "email": "", "is_active": false, "groups": [], "attributes": {}}""")),
            out _);

        var user = await sut.GetUserAsync("12");

        user!.TenantId.Should().BeNull();
        user.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task GetUserAsync_NotFound_ReturnsNull()
    {
        var sut = CreateClient(new FakeHttpMessageHandler(
            JsonResponse(HttpStatusCode.NotFound, """{"detail":"No User matches the given query."}""")), out _);

        (await sut.GetUserAsync("12")).Should().BeNull();
    }

    // The tenant check must run against the requested user: an answer about another pk is no record
    // of it.
    [Fact]
    public async Task GetUserAsync_ReturnedPkDiffersFromTheRequestedOne_ReturnsNull()
    {
        var sut = CreateClient(new FakeHttpMessageHandler(JsonResponse(HttpStatusCode.OK, UserTwelve)), out _);

        (await sut.GetUserAsync("13")).Should().BeNull();
    }

    [Fact]
    public async Task GetUserAsync_OtherFailure_ThrowsHttpRequestException()
    {
        var sut = CreateClient(new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.Forbidden)), out _);

        await sut.Invoking(s => s.GetUserAsync("12")).Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task DeactivateUserAsync_SendsPatchWithIsActiveFalse()
    {
        var response = JsonResponse(HttpStatusCode.OK, """{"pk":5,"is_active":false}""");
        var sut = CreateClient(new FakeHttpMessageHandler(response), out var handler);

        await sut.DeactivateUserAsync("5");

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Patch);
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/api/v3/core/users/5/");
        using var body = JsonDocument.Parse(handler.RequestBodies[0]!);
        body.RootElement.GetProperty("is_active").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task DeactivateAllUsersInTenantAsync_ListsThenDeactivatesEachMatchingUser()
    {
        var listResponse = JsonResponse(HttpStatusCode.OK, """
            {
              "pagination": {"next": 0},
              "results": [
                {"pk": 1, "uid": "uid-alice", "username": "alice", "email": "alice@example.invalid", "attributes": {"tenant_id": "tenant-a"}},
                {"pk": 2, "uid": "uid-bob", "username": "bob", "email": "bob@example.invalid", "attributes": {"tenant_id": "tenant-a"}}
              ]
            }
            """);
        var sut = CreateClient(
            new FakeHttpMessageHandler(
                listResponse,
                JsonResponse(HttpStatusCode.OK, "{}"),
                JsonResponse(HttpStatusCode.OK, "{}")),
            out var handler);

        await sut.DeactivateAllUsersInTenantAsync("tenant-a");

        handler.Requests.Should().HaveCount(3);
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
        handler.Requests[1].Method.Should().Be(HttpMethod.Patch);
        handler.Requests[1].RequestUri!.AbsolutePath.Should().Be("/api/v3/core/users/1/");
        handler.Requests[2].Method.Should().Be(HttpMethod.Patch);
        handler.Requests[2].RequestUri!.AbsolutePath.Should().Be("/api/v3/core/users/2/");
    }

    private const string TenantAdminsPk = "22222222-2222-2222-2222-222222222222";

    private static HttpResponseMessage TenantAdminsLookup() => JsonResponse(HttpStatusCode.OK,
        $$"""{"pagination":{"next":0},"results":[{"pk":"{{TenantAdminsPk}}","name":"tenant-admins"}]}""");

    /// <summary>
    /// CSR round 12 §2.4: membership goes through the group's own add_user/remove_user actions,
    /// which honour the object-level permission the orchestrator holds on tenant-admins only. The
    /// user PATCH of <c>groups</c> checked no permission on the group, and is gone.
    /// </summary>
    [Fact]
    public async Task AddGroupAsync_ResolvesTheGroupThenPostsTheUserToItsAddUserAction()
    {
        var sut = CreateClient(
            new FakeHttpMessageHandler(TenantAdminsLookup(), new HttpResponseMessage(HttpStatusCode.NoContent)),
            out var handler);

        await sut.AddGroupAsync("9", "tenant-admins");

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].RequestUri!.PathAndQuery.Should().Be("/api/v3/core/groups/?name=tenant-admins&include_users=false");
        handler.Requests[1].Method.Should().Be(HttpMethod.Post);
        handler.Requests[1].RequestUri!.AbsolutePath.Should().Be($"/api/v3/core/groups/{TenantAdminsPk}/add_user/");
        using var body = JsonDocument.Parse(handler.RequestBodies[1]!);
        body.RootElement.GetProperty("pk").GetInt32().Should().Be(9);
        handler.Requests.Should().NotContain(
            r => r.Method == HttpMethod.Patch || r.RequestUri!.AbsolutePath.StartsWith("/api/v3/core/users/"));
    }

    [Fact]
    public async Task RemoveGroupAsync_ResolvesTheGroupThenPostsTheUserToItsRemoveUserAction()
    {
        var sut = CreateClient(
            new FakeHttpMessageHandler(TenantAdminsLookup(), new HttpResponseMessage(HttpStatusCode.NoContent)),
            out var handler);

        await sut.RemoveGroupAsync("9", "tenant-admins");

        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Method.Should().Be(HttpMethod.Post);
        handler.Requests[1].RequestUri!.AbsolutePath.Should().Be($"/api/v3/core/groups/{TenantAdminsPk}/remove_user/");
        using var body = JsonDocument.Parse(handler.RequestBodies[1]!);
        body.RootElement.GetProperty("pk").GetInt32().Should().Be(9);
        handler.Requests.Should().NotContain(
            r => r.Method == HttpMethod.Patch || r.RequestUri!.AbsolutePath.StartsWith("/api/v3/core/users/"));
    }

    [Fact]
    public async Task AddGroupAsync_ActionRefused_ThrowsHttpRequestExceptionCarryingAuthentiksAnswer()
    {
        var sut = CreateClient(
            new FakeHttpMessageHandler(
                TenantAdminsLookup(),
                JsonResponse(HttpStatusCode.Forbidden, """{"detail":"You do not have permission to perform this action."}""")),
            out _);

        await sut.Invoking(s => s.AddGroupAsync("9", "tenant-admins"))
                 .Should().ThrowAsync<HttpRequestException>()
                 .WithMessage("*403*permission*");
    }

    [Fact]
    public async Task AddGroupAsync_SendsBodyWithContentLength_NotChunked()
    {
        var sut = CreateClient(
            new FakeHttpMessageHandler(TenantAdminsLookup(), new HttpResponseMessage(HttpStatusCode.NoContent)),
            out var handler);

        await sut.AddGroupAsync("9", "tenant-admins");

        var post = handler.BodyFraming.Should().ContainSingle().Subject;
        post.ContentLength.Should().NotBeNull();
        post.Chunked.Should().NotBe(true);
    }

    [Fact]
    public async Task ResolveGroupPk_GroupNotFound_Throws()
    {
        var emptyLookup = JsonResponse(HttpStatusCode.OK, """{"pagination":{"next":0},"results":[]}""");
        var sut = CreateClient(new FakeHttpMessageHandler(emptyLookup), out _);

        await sut.Invoking(s => s.AddGroupAsync("9", "does-not-exist"))
                 .Should().ThrowAsync<InvalidOperationException>()
                 .WithMessage("*does-not-exist*");
    }

    [Fact]
    public async Task DeactivateUserAsync_ThrowsHttpRequestException_OnNonSuccessStatusCode()
    {
        var sut = CreateClient(
            new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.NotFound)), out _);

        await sut.Invoking(s => s.DeactivateUserAsync("missing"))
                 .Should().ThrowAsync<HttpRequestException>();
    }

    /// <summary>
    /// Every request body must carry a Content-Length. PostAsJsonAsync/JsonContent serialize
    /// lazily and leave it unset, which makes HttpClient fall back to
    /// <c>Transfer-Encoding: chunked</c> — and Authentik's ASGI server silently DISCARDS a chunked
    /// request body. DRF then rejects the call with "This field is required." for every required
    /// field while the fields were in fact sent, so the failure names the wrong cause entirely.
    /// Verified against a live instance: byte-identical JSON succeeds with Content-Length and
    /// fails chunked.
    /// </summary>
    [Fact]
    public async Task CreateUserAsync_SendsBodiesWithContentLength_NotChunked()
    {
        var groupLookup = JsonResponse(HttpStatusCode.OK,
            """{"pagination":{"next":0},"results":[{"pk":"11111111-1111-1111-1111-111111111111","name":"tenant-admins"}]}""");
        var createUser = JsonResponse(HttpStatusCode.Created, """{"pk":42,"username":"new-user"}""");
        var recovery = JsonResponse(HttpStatusCode.OK, """{"link":"http://authentik.local/if/flow/recovery/abc"}""");
        var sut = CreateClient(new FakeHttpMessageHandler(groupLookup, createUser, recovery), out var handler);

        await sut.CreateUserAsync("new-user", "new-user@example.invalid", "tenant-1", ["tenant-admins"]);

        handler.BodyFraming.Should().HaveCount(2); // create user + trigger recovery
        foreach (var (uri, contentLength, chunked) in handler.BodyFraming)
        {
            contentLength.Should().NotBeNull($"{uri} must carry Content-Length, not be sent chunked");
            chunked.Should().NotBe(true, $"{uri} must not be sent chunked");
        }
    }

    [Fact]
    public async Task SetActiveAsync_SendsBodyWithContentLength_NotChunked()
    {
        var sut = CreateClient(
            new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)), out var handler);

        await sut.DeactivateUserAsync("42");

        var patch = handler.BodyFraming.Should().ContainSingle().Subject;
        patch.ContentLength.Should().NotBeNull("the PATCH body must carry Content-Length");
        patch.Chunked.Should().NotBe(true);
    }
}
