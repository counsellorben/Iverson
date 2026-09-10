using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Iverson.Api.Tests.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iverson.Api.Tests;

// Integration-level coverage for the /v1/traces relay (Program.cs): it bounds the body size
// and allow-lists content types before forwarding to Jaeger. Boots the real
// WebApplicationFactory<Program> host (see AuthTestWebApplicationFactory) with the
// "JaegerOtlpHttp" named client's primary handler swapped for a fake so no real Jaeger needs
// to be reachable, and asserts both the response the relay produces AND whether the fake was
// ever invoked — the latter distinguishes "rejected before forwarding" from "forwarded, and
// Jaeger happened to answer this way".
public class TracesRelayEndpointTests : IClassFixture<AuthTestWebApplicationFactory>
{
    private readonly AuthTestWebApplicationFactory _baseFactory;

    public TracesRelayEndpointTests(AuthTestWebApplicationFactory factory)
    {
        _baseFactory = factory;
    }

    private sealed class FakeJaegerHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        }
    }

    private (HttpClient Client, FakeJaegerHandler JaegerHandler) CreateAuthenticatedClient()
    {
        var jaegerHandler = new FakeJaegerHandler();
        var factory = _baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Overrides the primary handler of the "JaegerOtlpHttp" named client
                // registered in Program.cs (BaseAddress stays as Program.cs configured it;
                // only the transport is replaced), so the relay's outbound call never hits a
                // real network — this suite has no live Jaeger.
                services.AddHttpClient("JaegerOtlpHttp")
                    .ConfigurePrimaryHttpMessageHandler(() => jaegerHandler);
            });
        });

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestJwtFactory.CreateToken("test-service-audience", "trace-relay-test-user"));
        return (client, jaegerHandler);
    }

    [Fact]
    public async Task PostTraces_JsonBody_IsAccepted()
    {
        var (client, jaegerHandler) = CreateAuthenticatedClient();

        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("""{"resourceSpans":[]}"""));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var response = await client.PostAsync("/v1/traces", content);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, "the fake Jaeger's own status should be relayed back");
        jaegerHandler.CallCount.Should().Be(1, "a JSON body must be forwarded, not rejected");
    }

    [Fact]
    public async Task PostTraces_ProtobufBody_IsAccepted()
    {
        var (client, jaegerHandler) = CreateAuthenticatedClient();

        using var content = new ByteArrayContent([0x0a, 0x00]); // arbitrary bytes; the relay must not parse them
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");

        var response = await client.PostAsync("/v1/traces", content);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        jaegerHandler.CallCount.Should().Be(1, "a protobuf body must still be forwarded — restricting to JSON alone would break a proto exporter");
    }

    [Fact]
    public async Task PostTraces_UnrecognisedContentType_Returns415_AndIsNeverForwarded()
    {
        var (client, jaegerHandler) = CreateAuthenticatedClient();

        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("<xml/>"));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");

        var response = await client.PostAsync("/v1/traces", content);

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        jaegerHandler.CallCount.Should().Be(0, "an unrecognised content type must be rejected before any relay attempt");
    }

    [Fact]
    public async Task PostTraces_OversizedBody_Returns413_AndIsNeverForwarded()
    {
        var (client, jaegerHandler) = CreateAuthenticatedClient();

        // One byte past the endpoint's 1 MiB cap. ByteArrayContent sets a real Content-Length
        // header, which is exactly what the endpoint's declared-length check reads — this
        // does not depend on Kestrel-specific transport enforcement (which the in-memory
        // TestServer used here does not implement).
        var oversized = new byte[(1 * 1024 * 1024) + 1];
        using var content = new ByteArrayContent(oversized);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var response = await client.PostAsync("/v1/traces", content);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        jaegerHandler.CallCount.Should().Be(0, "an oversized body must be rejected before any bytes are relayed to Jaeger");
    }

    [Fact]
    public async Task PostTraces_BodyAtExactlyTheLimit_IsAccepted()
    {
        var (client, jaegerHandler) = CreateAuthenticatedClient();

        var atLimit = new byte[1 * 1024 * 1024];
        using var content = new ByteArrayContent(atLimit);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var response = await client.PostAsync("/v1/traces", content);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, "the cap must reject bodies OVER the limit, not bodies AT it");
        jaegerHandler.CallCount.Should().Be(1);
    }
}
