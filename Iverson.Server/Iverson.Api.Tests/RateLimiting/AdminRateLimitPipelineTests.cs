using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using FluentAssertions;
using Iverson.Api.Tests.Helpers;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iverson.Api.Tests.RateLimiting;

/// <summary>
/// CSR round 12 §2.1: the Operator HTTP admin routes share one <c>"admin"</c> budget of 30 requests
/// per minute per caller (<c>sub</c>), across all of them, on top of the global limiters.
/// <c>/health</c> is not an admin route.
/// </summary>
public class AdminRateLimitPipelineTests(AuthTestWebApplicationFactory baseFactory) : IClassFixture<AuthTestWebApplicationFactory>
{
    // Operator by the admin scope.
    private static string OperatorToken(string subject) => TestJwtFactory.CreateToken(
        "test-service-audience", subject, extraClaims: [new Claim("scope", "openid admin profile")]);

    private static async Task<HttpStatusCode> GetAsync(HttpClient client, string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    [Fact]
    public void EveryAdminRoute_CarriesTheAdminPolicy_AndHealthDoesNot()
    {
        var endpoints = baseFactory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .ToList();
        var admin = endpoints
            .Where(e => e.RoutePattern.RawText?.StartsWith("/admin/", StringComparison.Ordinal) == true)
            .ToList();

        admin.Select(e => e.RoutePattern.RawText).Should().Contain(new[]
        {
            "/admin/reconcile/{typeName}", "/admin/dlq", "/admin/dlq/{id}/replay",
            "/admin/console/tenants", "/admin/console/schema", "/admin/console/data-volume",
            "/admin/console/qdrant", "/admin/console/metrics",
        });
        admin.Should().OnlyContain(e => e.Metadata.GetMetadata<EnableRateLimitingAttribute>() != null
                                        && e.Metadata.GetMetadata<EnableRateLimitingAttribute>()!.PolicyName == "admin");
        endpoints.Single(e => e.RoutePattern.RawText == "/health")
            .Metadata.GetMetadata<EnableRateLimitingAttribute>().Should().BeNull();
    }

    [Fact]
    public async Task The_31st_admin_request_within_a_minute_is_rejected_whichever_admin_route_it_names()
    {
        // A host of its own, so its limiter starts empty.
        using var factory = baseFactory.WithWebHostBuilder(_ => { });
        var client = factory.CreateClient();
        var token = OperatorToken("admin-http-limit-operator");

        for (var i = 0; i < 30; i++)
            (await GetAsync(client, "/admin/console/tenants", token))
                .Should().Be(HttpStatusCode.OK, $"request {i + 1} of 30 is within the budget");

        (await GetAsync(client, "/admin/console/qdrant", token)).Should().Be(HttpStatusCode.TooManyRequests);

        // The bucket is the caller's: another operator is unaffected.
        (await GetAsync(client, "/admin/console/tenants", OperatorToken("admin-http-other-operator")))
            .Should().Be(HttpStatusCode.OK);
    }
}
