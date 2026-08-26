using System.Net;
using FluentAssertions;
using Iverson.Api;
using Iverson.Api.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iverson.Api.Tests;

// Regression coverage for Task 14's listener binding in Program.cs: the operational endpoints
// (/metrics, /health, /health/live and the four /probe/*) must not answer on the gRPC listener,
// which the main api ingress publishes under a bare `/` prefix, while continuing to answer on
// the Http1 listener that the kubelet probes, Prometheus and the admin-api ingress all use.
//
// What these tests actually pin, precisely: WebApplicationFactory.CreateClient() goes through
// TestServer's in-memory transport, which has no socket and therefore no local port at all —
// every request it makes arrives with HttpContext.Connection.LocalPort == 0, so an HttpClient
// cannot express "this request arrived on 8080" versus "on 8081". TestServer.SendAsync can:
// it hands the caller the HttpContext before the pipeline runs, and setting
// context.Connection.LocalPort writes the same IHttpConnectionFeature that Kestrel populates
// from the accepting socket in production and that Program.cs's middleware reads. So these
// tests exercise the real Program.cs pipeline (routing, the listener middleware, authentication,
// authorization, the endpoint itself) against a synthesised local port rather than two real
// listening sockets. What is NOT covered here is Kestrel's own binding — that appsettings.json
// really does put the Http2 endpoint on 8080 and the Http1 endpoint on 8081 — which no
// in-process test can observe.
public class OperationalListenerBindingPipelineTests : IClassFixture<AuthTestWebApplicationFactory>
{
    private const int GrpcListenerPort = 8080;
    private const int HttpListenerPort = 8081;

    private readonly AuthTestWebApplicationFactory _factory;

    public OperationalListenerBindingPipelineTests(AuthTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private Task<HttpContext> SendAsync(string method, string path, int localPort) =>
        _factory.Server.SendAsync(context =>
        {
            context.Request.Method = method;
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("localhost");
            context.Request.Path = path;
            context.Connection.LocalPort = localPort;
        });

    // /health is included here and not in the "still answers" theory below for the same reason
    // ProbeAuthorizationPipelineTests excludes it: its handler fans out to real
    // Postgres/StarRocks/Qdrant/Kafka. On the gRPC listener the middleware short-circuits before
    // the handler runs, so asking for it here is safe and is exactly the assertion that matters.
    [Theory]
    [InlineData("GET", "/metrics")]
    [InlineData("GET", "/health")]
    [InlineData("GET", "/health/live")]
    [InlineData("GET", "/probe/sql")]
    [InlineData("GET", "/probe/starrocks")]
    [InlineData("GET", "/probe/vector")]
    [InlineData("POST", "/probe/kafka")]
    public async Task RequestOnGrpcListener_OperationalEndpoint_Returns404(string method, string path)
    {
        var context = await SendAsync(method, path, GrpcListenerPort);

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    // The other half of the pin: the identical request on the Http1 listener still reaches the
    // endpoint. /metrics and /health/live must be 200 (anonymous — Prometheus and the kubelet
    // liveness probe call them with no credentials), and the four probes must be 401 rather
    // than 404: 401 is the Operator gate rejecting an anonymous caller, which can only happen
    // if routing selected the endpoint and the listener middleware let it through.
    [Theory]
    [InlineData("GET", "/metrics", HttpStatusCode.OK)]
    [InlineData("GET", "/health/live", HttpStatusCode.OK)]
    [InlineData("GET", "/probe/sql", HttpStatusCode.Unauthorized)]
    [InlineData("GET", "/probe/starrocks", HttpStatusCode.Unauthorized)]
    [InlineData("GET", "/probe/vector", HttpStatusCode.Unauthorized)]
    [InlineData("POST", "/probe/kafka", HttpStatusCode.Unauthorized)]
    public async Task RequestOnHttpListener_OperationalEndpoint_IsServed(string method, string path, HttpStatusCode expected)
    {
        var context = await SendAsync(method, path, HttpListenerPort);

        context.Response.StatusCode.Should().Be((int)expected);
    }

    // Scope control. The listener binding is deliberately NOT applied to the endpoints that are
    // not operational surfaces, so these must still be reachable on the gRPC listener: 401 (the
    // authorization gate answering) rather than 404 (the listener middleware short-circuiting).
    // Without this, a filter wrongly applied app-wide — or applied by path prefix instead of by
    // endpoint metadata — would pass the two theories above and still break the console.
    [Theory]
    [InlineData("GET", "/admin/dlq")]
    [InlineData("GET", "/admin/console/tenants")]
    [InlineData("POST", "/v1/traces")]
    public async Task RequestOnGrpcListener_NonOperationalEndpoint_StillReachesAuthorization(string method, string path)
    {
        var context = await SendAsync(method, path, GrpcListenerPort);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    // Drift guard, and the reason the two theories above are not sufficient on their own: their
    // seven paths are enumerated by hand, by the same hand that attaches the metadata in
    // Program.cs. An eighth /probe/* added next month would ship answering on the gRPC listener
    // with the whole suite green, because no InlineData would name it. This asserts over the
    // app's real EndpointDataSource that the set of endpoints carrying HttpListenerOnly is
    // EXACTLY these seven route patterns, which fails in both directions: a marker missing from
    // a new operational endpoint, and a marker landing somewhere it must not (a gRPC service,
    // /v1/traces, /admin/console/*). Route patterns rather than endpoint names because
    // MapPrometheusScrapingEndpoint sets no name.
    [Fact]
    public void HttpListenerOnlyMetadata_IsOnExactlyTheOperationalEndpoints()
    {
        string[] expected =
        [
            "/health",
            "/health/live",
            "/metrics",
            "/probe/kafka",
            "/probe/sql",
            "/probe/starrocks",
            "/probe/vector"
        ];

        var marked = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<HttpListenerOnly>() is not null)
            .Select(endpoint => endpoint is RouteEndpoint route
                ? "/" + route.RoutePattern.RawText!.TrimStart('/')
                : endpoint.DisplayName ?? endpoint.ToString()!)
            .OrderBy(pattern => pattern, StringComparer.Ordinal)
            .ToArray();

        marked.Should().Equal(expected);
    }
}
