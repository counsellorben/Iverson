using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Iverson.Api.Tests.Helpers;
using Xunit;

namespace Iverson.Api.Tests;

/// <summary>
/// <c>GET /admin/console/metrics</c> against the real <c>Program.cs</c> pipeline: <see cref="AuthTestWebApplicationFactory"/>
/// boots the real <c>JwtBearer</c> handler and the real <c>Operator</c> policy, with no DI swap
/// needed for <c>IPrometheusQueryClient</c>. The test host's <c>appsettings.json</c> ships
/// <c>Prometheus:BaseUrl: ""</c> (no override here), which is itself a real, supported deployment
/// state — the same one <c>values-laptop.yaml</c> ships — so an authorized request reaching the
/// handler proves the whole wire-up (config key → <c>PrometheusOptions</c> → handler → JSON body)
/// without needing a real or faked Prometheus. The three OTHER states (unreachable, and Prometheus
/// actually answering) are exercised at the handler level in
/// <c>AdminConsoleMetricsEndpointTests</c>, where a fake <c>IPrometheusQueryClient</c> can stage
/// them — booting a whole second host per state would buy nothing this file's authorization
/// coverage doesn't already need.
/// </summary>
public class AdminConsoleMetricsPipelineTests : IClassFixture<AuthTestWebApplicationFactory>
{
    private const string Metrics = "/admin/console/metrics";

    private readonly HttpClient _client;

    public AdminConsoleMetricsPipelineTests(AuthTestWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    private static void Authorize(HttpRequestMessage request, string? token)
    {
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    [Fact]
    public async Task AnonymousRequest_Returns401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Metrics);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AuthenticatedNonOperator_Returns403()
    {
        var token = TestJwtFactory.CreateToken("test-service-audience", "ak-some-other-service");
        using var request = new HttpRequestMessage(HttpMethod.Get, Metrics);
        Authorize(request, token);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// THE END-TO-END WIRING ASSERTION. Passing the Operator gate and reaching a
    /// 503/"notDeployed" — not a 401/403 from the gate, and not a 500 from a DI-resolution
    /// failure or an unhandled exception in the handler — proves
    /// <c>Program.cs</c>'s <c>Configure&lt;PrometheusOptions&gt;</c> and named-HttpClient
    /// registrations, <c>MapAdminConsoleMetricsEndpoint</c>'s route/policy, and the handler's
    /// own "no HTTP call attempted when unconfigured" branch all thread together correctly
    /// against the test host's real (empty) <c>Prometheus:BaseUrl</c>.
    /// </summary>
    [Fact]
    public async Task AuthenticatedOperator_PrometheusNotConfigured_Returns503WithNotDeployedReason()
    {
        var token = TestJwtFactory.CreateToken(
            "test-service-audience", "human-operator", extraClaims: [new Claim("groups", "operators")]);
        using var request = new HttpRequestMessage(HttpMethod.Get, Metrics);
        Authorize(request, token);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("reason").GetString().Should().Be("notDeployed");
    }

    /// <summary>The global constraint, colocated with the diff that could break it (mirrors AdminConsoleEndpointsPipelineTests' identical assertion).</summary>
    [Fact]
    public async Task AnonymousGet_Metrics_ScrapeEndpoint_StillReturns200()
    {
        var response = await _client.GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
