using System.Net;
using FluentAssertions;
using Iverson.Api.Tests.Helpers;
using Xunit;

namespace Iverson.Api.Tests;

// Integration-level regression coverage for the JwtBearer + FallbackPolicy wiring in
// Program.cs. OperatorAuthorizationPolicyTests only exercises the pure
// OperatorAuthorizationPolicy.IsSatisfiedBy predicate — it can't catch an endpoint that
// forgot to opt out of (or into) authorization, which is exactly the class of bug that let
// /metrics fall through the FallbackPolicy undetected. These tests boot the real
// WebApplicationFactory<Program> host (see AuthTestWebApplicationFactory) and hit endpoints
// with no Authorization header at all — no real JWTs are needed for either the
// AllowAnonymous assertions or the 401-rejection assertion.
public class AuthenticationPipelineTests : IClassFixture<AuthTestWebApplicationFactory>
{
    private readonly HttpClient _client;

    public AuthenticationPipelineTests(AuthTestWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task AnonymousGet_HealthLive_DoesNotReturn401()
    {
        var response = await _client.GetAsync("/health/live");

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnonymousGet_Metrics_DoesNotReturn401()
    {
        // Regression test for the Critical finding: MapPrometheusScrapingEndpoint() was not
        // exempted from the FallbackPolicy, so Prometheus scraping would have started
        // receiving 401s the moment this shipped.
        var response = await _client.GetAsync("/metrics");

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AnonymousGet_AdminDlq_Returns401()
    {
        // The task brief for this regression test described a POST to /admin/dlq, but that
        // route is actually MapGet (POST only exists on /admin/dlq/{id}/replay) — see
        // Program.cs. Testing the wrong verb would hit ASP.NET's routing/method-matching
        // before authorization even runs, asserting the wrong thing. This exercises the
        // real route with its real verb.
        var response = await _client.GetAsync("/admin/dlq");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
