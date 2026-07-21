using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Iverson.Api.Tenancy;
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

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            Requests.Add(request);
            RequestBodies.Add(request.Content is not null
                ? await request.Content.ReadAsStringAsync(ct)
                : null);

            var response = responses[Math.Min(_index, responses.Length - 1)];
            _index++;
            return response;
        }
    }

    private static AuthentikAdminClient CreateClient(FakeHttpMessageHandler handler, out FakeHttpMessageHandler exposedHandler)
    {
        exposedHandler = handler;
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(AuthentikAdminClient.HttpClientName)
               .Returns(_ => new HttpClient(handler) { BaseAddress = new Uri("http://authentik.local") });
        return new AuthentikAdminClient(factory);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task CreateUserAsync_ResolvesGroupThenCreatesUserThenSetsPassword()
    {
        var groupLookup = JsonResponse(HttpStatusCode.OK,
            """{"pagination":{"next":0},"results":[{"pk":"11111111-1111-1111-1111-111111111111","name":"tenant-admins"}]}""");
        var createUser = JsonResponse(HttpStatusCode.Created,
            """{"pk":42,"username":"new-user","email":"new-user@example.invalid"}""");
        var setPassword = JsonResponse(HttpStatusCode.OK, "{}");

        var sut = CreateClient(new FakeHttpMessageHandler(groupLookup, createUser, setPassword), out var handler);

        var userId = await sut.CreateUserAsync(
            "new-user", "new-user@example.invalid", "s3cret!", "tenant-a", ["tenant-admins"]);

        userId.Should().Be("42");

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
        handler.Requests[2].RequestUri!.AbsolutePath.Should().Be("/api/v3/core/users/42/set_password/");
        using (var body = JsonDocument.Parse(handler.RequestBodies[2]!))
        {
            body.RootElement.GetProperty("password").GetString().Should().Be("s3cret!");
        }
    }

    [Fact]
    public async Task CreateUserAsync_NoGroups_SkipsGroupResolution()
    {
        var createUser = JsonResponse(HttpStatusCode.Created,
            """{"pk":7,"username":"u","email":"u@example.invalid"}""");
        var setPassword = JsonResponse(HttpStatusCode.OK, "{}");

        var sut = CreateClient(new FakeHttpMessageHandler(createUser, setPassword), out var handler);

        var userId = await sut.CreateUserAsync("u", "u@example.invalid", "pw", "tenant-b", []);

        userId.Should().Be("7");
        handler.Requests.Should().HaveCount(2);
        using var body = JsonDocument.Parse(handler.RequestBodies[0]!);
        body.RootElement.GetProperty("groups").GetArrayLength().Should().Be(0);
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
        users[0].Should().BeEquivalentTo(new AuthentikUser("1", "alice", "alice@example.invalid"));
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

    [Fact]
    public async Task AddGroupAsync_ResolvesGroupThenPostsAddUser()
    {
        var groupLookup = JsonResponse(HttpStatusCode.OK,
            """{"pagination":{"next":0},"results":[{"pk":"22222222-2222-2222-2222-222222222222","name":"tenant-admins"}]}""");
        var addUser = JsonResponse(HttpStatusCode.OK, "{}");

        var sut = CreateClient(new FakeHttpMessageHandler(groupLookup, addUser), out var handler);

        await sut.AddGroupAsync("9", "tenant-admins");

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].RequestUri!.PathAndQuery.Should().Be("/api/v3/core/groups/?name=tenant-admins");
        handler.Requests[1].Method.Should().Be(HttpMethod.Post);
        handler.Requests[1].RequestUri!.AbsolutePath
            .Should().Be("/api/v3/core/groups/22222222-2222-2222-2222-222222222222/add_user/");
        using var body = JsonDocument.Parse(handler.RequestBodies[1]!);
        body.RootElement.GetProperty("pk").GetInt32().Should().Be(9);
    }

    [Fact]
    public async Task RemoveGroupAsync_ResolvesGroupThenPostsRemoveUser()
    {
        var groupLookup = JsonResponse(HttpStatusCode.OK,
            """{"pagination":{"next":0},"results":[{"pk":"33333333-3333-3333-3333-333333333333","name":"tenant-admins"}]}""");
        var removeUser = JsonResponse(HttpStatusCode.OK, "{}");

        var sut = CreateClient(new FakeHttpMessageHandler(groupLookup, removeUser), out var handler);

        await sut.RemoveGroupAsync("9", "tenant-admins");

        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Method.Should().Be(HttpMethod.Post);
        handler.Requests[1].RequestUri!.AbsolutePath
            .Should().Be("/api/v3/core/groups/33333333-3333-3333-3333-333333333333/remove_user/");
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
}
