using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
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

    [Fact]
    public async Task ServiceTokenOnly_GetAdminDlq_Returns401()
    {
        // Regression test: /admin/dlq used to only check the Operator policy against whichever
        // principal authenticated under the default scheme, so a valid service-to-service token
        // with an "operators" group claim would have satisfied RequireAuthorization("Operator")
        // and returned 200 with no acting-user context at all. The endpoint must also require a
        // separately-authenticated "ActingUser" token before it does anything else.
        var token = TestJwtFactory.CreateToken(
            "test-service-audience", "test-operator", extraClaims: [new Claim("groups", "operators")]);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/dlq");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ServiceTokenAndActingUserToken_GetAdminDlq_Succeeds()
    {
        var serviceToken = TestJwtFactory.CreateToken(
            "test-service-audience", "test-operator", extraClaims: [new Claim("groups", "operators")]);
        var actingUserToken = TestJwtFactory.CreateToken(
            "test-actinguser-audience", "test-user",
            extraClaims: [new Claim("tenant_id", "tenant-a"), new Claim("groups", "operators")]);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/dlq");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);
        request.Headers.Add("x-acting-user-authorization", $"Bearer {actingUserToken}");

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
