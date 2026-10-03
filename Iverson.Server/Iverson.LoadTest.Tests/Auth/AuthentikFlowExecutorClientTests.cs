using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Iverson.LoadTest.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Iverson.LoadTest.Tests.Auth;

public class AuthentikFlowExecutorClientTests
{
    private const string Link = "http://authentik-server:9000/if/flow/iverson-recovery/?flow_token=tok123";
    private const string ExecutorUrl =
        "http://localhost:9000/api/v3/flows/executor/iverson-recovery/?query=flow_token%3Dtok123";
    private const string Password = "dev-only-not-for-production-tenant-admin-password-0123456789";

    private sealed record Sent(HttpMethod Method, string Url, string? Host, string Body);

    // Answers each request with the next scripted response and records what was sent. HttpClient
    // itself never follows redirects (HttpClientHandler does), so this fake sees every 302 exactly
    // as the client's real AllowAutoRedirect = false handler returns it.
    private sealed class FakeHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new Sent(request.Method, request.RequestUri!.AbsoluteUri, request.Headers.Host, body));
            return responses[Requests.Count - 1];
        }
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // Authentik answers each completed stage with a body-less 302 back to the same executor URL.
    private static HttpResponseMessage Redirect() =>
        new(HttpStatusCode.Redirect)
        {
            Content = new StringContent("", Encoding.UTF8, "text/html"),
            Headers = { Location = new Uri("/api/v3/flows/executor/iverson-recovery/?query=flow_token%3Dtok123", UriKind.Relative) },
        };

    private static AuthentikFlowExecutorClient Client(FakeHandler handler) => new(
        new AuthentikIdentityConfig(
            "iverson-loadtest-tenant-admin", Password, "dev-iverson-loadtest-human-client-id",
            "http://localhost/placeholder-callback", "http://localhost:9000", "authentik-server:9000", "compose"),
        NullLogger<AuthentikFlowExecutorClient>.Instance,
        handler);

    [Fact]
    public async Task SetPasswordFromRecoveryLinkAsync_PostsThePassword_AndFollowsTheRedirectsToCompletion()
    {
        var handler = new FakeHandler(
            Json("""{"component":"ak-stage-prompt"}"""),
            Redirect(), Redirect(), Redirect(),
            Json("""{"component":"xak-flow-redirect","to":"/"}"""));

        await Client(handler).SetPasswordFromRecoveryLinkAsync(Link);

        handler.Requests.Should().HaveCount(5, "nothing is sent after the xak-flow-redirect");
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].Url.Should().Be(ExecutorUrl, "only the slug and flow_token come from the link, not its host");
        handler.Requests[0].Host.Should().Be("authentik-server:9000");
        handler.Requests[1].Method.Should().Be(HttpMethod.Post);
        handler.Requests[1].Url.Should().Be(ExecutorUrl);
        using (var body = JsonDocument.Parse(handler.Requests[1].Body))
        {
            body.RootElement.GetProperty("password").GetString().Should().Be(Password);
            body.RootElement.GetProperty("password_repeat").GetString().Should().Be(Password);
        }
        handler.Requests.Skip(2).Should().AllSatisfy(r =>
        {
            r.Method.Should().Be(HttpMethod.Get);
            r.Url.Should().Be(ExecutorUrl);
        });
    }

    [Fact]
    public async Task SetPasswordFromRecoveryLinkAsync_Throws_WithAuthentiksError_WhenThePasswordIsRejected()
    {
        var handler = new FakeHandler(
            Json("""{"component":"ak-stage-prompt"}"""),
            Json("""
                 {"component":"ak-stage-prompt","response_errors":{"non_field_errors":[
                   {"string":"Password needs to be 8 characters or longer.","code":"invalid"}]}}
                 """));

        var act = () => Client(handler).SetPasswordFromRecoveryLinkAsync(Link);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Password needs to be 8 characters or longer.*");
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task SetPasswordFromRecoveryLinkAsync_Throws_AndSendsNoPost_WhenThePromptIsNotOffered()
    {
        var handler = new FakeHandler(Json("""{"component":"ak-stage-access-denied"}"""));

        var act = () => Client(handler).SetPasswordFromRecoveryLinkAsync(Link);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ak-stage-access-denied*");
        handler.Requests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Get);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://authentik-server:9000/if/flow/iverson-recovery/")]
    public async Task SetPasswordFromRecoveryLinkAsync_Throws_AndSendsNothing_ForAMissingOrMalformedLink(string link)
    {
        var handler = new FakeHandler();

        var act = () => Client(handler).SetPasswordFromRecoveryLinkAsync(link);

        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SetPasswordFromRecoveryLinkAsync_Throws_WhenTheRedirectsEndAnywhereButCompletion()
    {
        var handler = new FakeHandler(
            Json("""{"component":"ak-stage-prompt"}"""),
            Redirect(), Redirect(),
            Json("""{"component":"ak-stage-access-denied"}"""));

        var act = () => Client(handler).SetPasswordFromRecoveryLinkAsync(Link);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ak-stage-access-denied*");
        handler.Requests.Should().HaveCount(4);
    }
}
