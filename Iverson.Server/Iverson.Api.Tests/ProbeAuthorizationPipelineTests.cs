using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using FluentAssertions;
using Iverson.Api.Tests.Helpers;
using Xunit;

namespace Iverson.Api.Tests;

// Regression coverage for Task 2's `.RequireAuthorization("Operator")` on the four probe
// endpoints in Program.cs. Boots the real Program.cs pipeline via AuthTestWebApplicationFactory
// (same pattern as AuthenticationPipelineTests / TenantLifecycleGrpcServiceAuthorizationPipelineTests)
// so app.UseAuthorization() actually runs.
public class ProbeAuthorizationPipelineTests : IClassFixture<AuthTestWebApplicationFactory>
{
    private readonly HttpClient _client;

    public ProbeAuthorizationPipelineTests(AuthTestWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    private static void Authorize(HttpRequestMessage request, string? token)
    {
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    [Theory]
    [InlineData("/probe/sql")]
    [InlineData("/probe/starrocks")]
    [InlineData("/probe/vector")]
    public async Task AnonymousRequest_Returns401(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AnonymousPost_ProbeKafka_Returns401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/probe/kafka");

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AuthenticatedWithNoGroupsOrAdminScope_ProbeStarRocks_Returns403()
    {
        var token = TestJwtFactory.CreateToken("test-service-audience", "ak-some-other-service");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/probe/starrocks");
        Authorize(request, token);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // /probe/starrocks is the probe used to exercise the "authorized succeeds" path against a
    // live request/response round-trip: IEngagementStoreHealthCheck.IsHealthyAsync catches its
    // own connection failures internally (EngagementHealthChecker.CheckHealthAsync) and reports
    // a normal 200 rather than throwing, so this test host — which has no real StarRocks —
    // still exercises the Operator gate letting the call all the way through to a real response,
    // the same way TenantLifecycleGrpcServiceAuthorizationPipelineTests uses ListTenants against
    // NoOpTenantRepository. /probe/sql, /probe/vector and /probe/kafka would instead attempt a
    // real Postgres/Qdrant/Kafka connection with no such internal catch, which is unnecessary
    // noise for an authorization-gate test already covered by the 401 cases above.
    [Fact]
    public async Task AuthenticatedWithOperatorsGroup_ProbeStarRocks_PassesAuthorizationGateAndReturnsResponse()
    {
        var token = TestJwtFactory.CreateToken(
            "test-service-audience",
            "human-operator",
            extraClaims: [new Claim("groups", "operators")]);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/probe/starrocks");
        Authorize(request, token);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AuthenticatedWithAdminScope_ProbeStarRocks_PassesAuthorizationGateAndReturnsResponse()
    {
        var token = TestJwtFactory.CreateToken(
            "test-service-audience",
            "ak-iverson-automation",
            extraClaims: [new Claim("scope", "openid admin profile")]);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/probe/starrocks");
        Authorize(request, token);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
