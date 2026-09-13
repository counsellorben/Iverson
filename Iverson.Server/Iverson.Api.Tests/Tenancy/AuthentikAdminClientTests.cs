using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Iverson.Api.Tenancy;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Tenancy;

public sealed class AuthentikAdminClientTests
{
    /// <summary>
    /// Mirrors EmbeddingServiceTests' FakeHttpMessageHandler, extended to return a queue of
    /// responses in order (AuthentikAdminClient often issues more than one HTTP call per logical
    /// operation, e.g. resolve-group-pk then create-user then set-password). If fewer responses
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
        out ILogger<IdpAdminClient> logger)
    {
        exposedHandler = handler;
        var factory = Substitute.For<IHttpClientFactory>();
        factory
            .CreateClient(IdpAdminClient.HttpClientName)
            .Returns(_ => new HttpClient(handler) { BaseAddress = new Uri("http://authentik.local") });
        logger = Substitute.For<ILogger<IdpAdminClient>>();
        return new IdpAdminClient(factory, logger);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

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
        handler.Requests[0].RequestUri!.PathAndQuery.Should().Be("/api/v3/core/groups/?name=tenant-admins");

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

    [Fact]
    public async Task ListUsersByTenantAsync_FiltersByAttributesTenantId()
    {
        var page = JsonResponse(HttpStatusCode.OK, """
            {
              "pagination": {"next": 0},
              "results": [
                {"pk": 1, "username": "alice", "email": "alice@example.invalid", "attributes": {"tenant_id": "tenant-a"}},
                {"pk": 2, "username": "bob", "email": "bob@example.invalid", "attributes": {"tenant_id": "tenant-b"}}
              ]
            }
            """);

        var sut = CreateClient(new FakeHttpMessageHandler(page), out var handler);

        var users = (await sut.ListUsersByTenantAsync("tenant-a")).ToList();

        users.Should().ContainSingle();
        users[0].Should().BeEquivalentTo(new IdpUser("1", "alice", "alice@example.invalid"));
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/api/v3/core/users/");
    }

    [Fact]
    public async Task ListUsersByTenantAsync_FollowsPagination_AcrossMultiplePages()
    {
        var page1 = JsonResponse(HttpStatusCode.OK, """
            {
              "pagination": {"next": 2},
              "results": [
                {"pk": 1, "username": "alice", "email": "alice@example.invalid", "attributes": {"tenant_id": "tenant-a"}}
              ]
            }
            """);
        var page2 = JsonResponse(HttpStatusCode.OK, """
            {
              "pagination": {"next": 0},
              "results": [
                {"pk": 3, "username": "carol", "email": "carol@example.invalid", "attributes": {"tenant_id": "tenant-a"}}
              ]
            }
            """);

        var sut = CreateClient(new FakeHttpMessageHandler(page1, page2), out var handler);

        var users = (await sut.ListUsersByTenantAsync("tenant-a")).ToList();

        users.Should().HaveCount(2);
        users.Select(u => u.Username).Should().BeEquivalentTo(["alice", "carol"]);
        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].RequestUri!.PathAndQuery.Should().Be("/api/v3/core/users/?page=2");
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
                {"pk": 1, "username": "alice", "email": "alice@example.invalid", "attributes": {"tenant_id": "tenant-a"}}
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
                {"pk": 1, "username": "alice", "email": "alice@example.invalid", "attributes": {"tenant_id": "tenant-a"}}
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
                {"pk": 1, "username": "alice", "email": "alice@example.invalid", "attributes": {"tenant_id": "tenant-a"}}
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
                {"pk": 1, "username": "alice", "email": "alice@example.invalid", "attributes": {"tenant_id": "tenant-a"}}
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
                {"pk": 1, "username": "alice", "email": "alice@example.invalid", "attributes": {"tenant_id": "tenant-a"}}
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
                {"pk": 1, "username": "alice", "email": "alice@example.invalid", "attributes": {"tenant_id": "tenant-a"}},
                {"pk": 2, "username": "bob", "email": "bob@example.invalid", "attributes": {"tenant_id": "tenant-a"}}
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
    private const string OtherGroupPk = "44444444-4444-4444-4444-444444444444";

    private static HttpResponseMessage TenantAdminsLookup() => JsonResponse(HttpStatusCode.OK,
        $$"""{"pagination":{"next":0},"results":[{"pk":"{{TenantAdminsPk}}","name":"tenant-admins"}]}""");

    private static HttpResponseMessage UserWithGroups(params string[] groupPks) => JsonResponse(
        HttpStatusCode.OK,
        $$"""{"pk":9,"username":"u","groups":[{{string.Join(",", groupPks.Select(pk => $"\"{pk}\""))}}]}""");

    /// <summary>
    /// CSR round-3 finding #1: membership must NOT go through
    /// <c>POST /api/v3/core/groups/{pk}/add_user/</c>. That endpoint is gated on the global
    /// <c>authentik_core.add_user_to_group</c> permission, which means "add any user to any
    /// group" — including <c>authentik Admins</c>, with no superuser check on that code path —
    /// so the orchestrator role no longer holds it. Membership is written through the user
    /// instead, which needs only the object-scoped <c>change_user</c>.
    /// </summary>
    [Fact]
    public async Task AddGroupAsync_ResolvesGroupThenPatchesTheUsersGroupList()
    {
        var sut = CreateClient(
            new FakeHttpMessageHandler(
                TenantAdminsLookup(),
                UserWithGroups(OtherGroupPk),
                JsonResponse(HttpStatusCode.OK, "{}")),
            out var handler);

        await sut.AddGroupAsync("9", "tenant-admins");

        handler.Requests.Should().HaveCount(3);
        handler.Requests[0].RequestUri!.PathAndQuery.Should().Be("/api/v3/core/groups/?name=tenant-admins");
        handler.Requests[1].Method.Should().Be(HttpMethod.Get);
        handler.Requests[1].RequestUri!.AbsolutePath.Should().Be("/api/v3/core/users/9/");
        handler.Requests[2].Method.Should().Be(HttpMethod.Patch);
        handler.Requests[2].RequestUri!.AbsolutePath.Should().Be("/api/v3/core/users/9/");

        handler.Requests.Should().NotContain(
            r => r.RequestUri!.AbsolutePath.Contains("/add_user/"),
            "the group add_user action requires the global add_user_to_group permission that CSR " +
            "round-3 finding #1 removed from the orchestrator role");

        using var body = JsonDocument.Parse(handler.RequestBodies[2]!);
        var groups = body.RootElement.GetProperty("groups").EnumerateArray().Select(g => g.GetString()).ToArray();
        groups.Should().BeEquivalentTo([OtherGroupPk, TenantAdminsPk],
            "the PATCH replaces the whole list, so every group the user already had must be carried over");
    }

    [Fact]
    public async Task AddGroupAsync_UserAlreadyInGroup_DoesNotPatch()
    {
        var sut = CreateClient(
            new FakeHttpMessageHandler(TenantAdminsLookup(), UserWithGroups(TenantAdminsPk)),
            out var handler);

        await sut.AddGroupAsync("9", "tenant-admins");

        handler.Requests.Should().HaveCount(2, "a no-change membership write should spend no PATCH");
        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task RemoveGroupAsync_ResolvesGroupThenPatchesTheGroupOutOfTheUsersList()
    {
        var sut = CreateClient(
            new FakeHttpMessageHandler(
                TenantAdminsLookup(),
                UserWithGroups(OtherGroupPk, TenantAdminsPk),
                JsonResponse(HttpStatusCode.OK, "{}")),
            out var handler);

        await sut.RemoveGroupAsync("9", "tenant-admins");

        handler.Requests.Should().HaveCount(3);
        handler.Requests[2].Method.Should().Be(HttpMethod.Patch);
        handler.Requests[2].RequestUri!.AbsolutePath.Should().Be("/api/v3/core/users/9/");
        handler.Requests.Should().NotContain(r => r.RequestUri!.AbsolutePath.Contains("/remove_user/"));

        using var body = JsonDocument.Parse(handler.RequestBodies[2]!);
        var groups = body.RootElement.GetProperty("groups").EnumerateArray().Select(g => g.GetString()).ToArray();
        groups.Should().BeEquivalentTo([OtherGroupPk],
            "removing one group must not drop the user's other memberships");
    }

    [Fact]
    public async Task RemoveGroupAsync_UserNotInGroup_DoesNotPatch()
    {
        var sut = CreateClient(
            new FakeHttpMessageHandler(TenantAdminsLookup(), UserWithGroups(OtherGroupPk)),
            out var handler);

        await sut.RemoveGroupAsync("9", "tenant-admins");

        handler.Requests.Should().HaveCount(2);
        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Patch);
    }

    /// <summary>
    /// The groups PATCH is a whole-list write, so an unrecognised user payload must abort rather
    /// than PATCH a list rebuilt from nothing — that would silently strip every other group the
    /// user belongs to.
    /// </summary>
    [Theory]
    [InlineData("""{"pk":9,"username":"u"}""")]                 // no "groups" property at all
    [InlineData("""{"pk":9,"username":"u","groups":null}""")]   // present but not an array
    [InlineData("""{"pk":9,"username":"u","groups":"none"}""")]
    public async Task AddGroupAsync_UserResponseHasNoGroupsArray_ThrowsWithoutPatching(string userJson)
    {
        var sut = CreateClient(
            new FakeHttpMessageHandler(TenantAdminsLookup(), JsonResponse(HttpStatusCode.OK, userJson)),
            out var handler);

        await sut.Invoking(s => s.AddGroupAsync("9", "tenant-admins"))
                 .Should().ThrowAsync<InvalidOperationException>()
                 .WithMessage("*groups*");

        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task AddGroupAsync_UserReadFails_ThrowsHttpRequestException()
    {
        var sut = CreateClient(
            new FakeHttpMessageHandler(TenantAdminsLookup(), new HttpResponseMessage(HttpStatusCode.Forbidden)),
            out var handler);

        await sut.Invoking(s => s.AddGroupAsync("9", "tenant-admins"))
                 .Should().ThrowAsync<HttpRequestException>();

        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task AddGroupAsync_SendsPatchBodyWithContentLength_NotChunked()
    {
        var sut = CreateClient(
            new FakeHttpMessageHandler(
                TenantAdminsLookup(),
                UserWithGroups(),
                JsonResponse(HttpStatusCode.OK, "{}")),
            out var handler);

        await sut.AddGroupAsync("9", "tenant-admins");

        handler.BodyFraming.Should().HaveCount(1);
        handler.BodyFraming[0].ContentLength.Should().NotBeNull();
        handler.BodyFraming[0].Chunked.Should().NotBe(true);
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
