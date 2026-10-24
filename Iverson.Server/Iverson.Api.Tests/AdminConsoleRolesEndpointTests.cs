using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Iverson.Api.Tests.Helpers;
using Xunit;

namespace Iverson.Api.Tests;

/// <summary>
/// CSR round 12 §2.2: <c>GET /admin/console/roles</c>, which the console's role guard reads. It answers the
/// caller's roles with the server's own functions, for console tokens only (<c>ConsoleClient</c>), and with
/// no active-tenant filter, because an operator carries no <c>tenant_id</c>. Through the real
/// <c>Program.cs</c> pipeline, whose test host lists <see cref="AuthTestWebApplicationFactory.HumanOperatorSubject"/>.
/// </summary>
public class AdminConsoleRolesEndpointTests : IClassFixture<AuthTestWebApplicationFactory>
{
    private const string Roles = "/admin/console/roles";

    private readonly HttpClient _client;

    public AdminConsoleRolesEndpointTests(AuthTestWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    private static string ConsoleToken(string subject, params Claim[] claims) =>
        TestJwtFactory.CreateToken(AuthTestWebApplicationFactory.ConsoleAudience, subject, extraClaims: claims);

    private async Task<HttpResponseMessage> GetAsync(string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Roles);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task AnonymousRequest_Returns401()
    {
        var response = await GetAsync(token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ATokenTheConsoleDidNotIssue_Returns403()
    {
        var token = TestJwtFactory.CreateToken("test-service-audience", AuthTestWebApplicationFactory.HumanOperatorSubject);

        var response = await GetAsync(token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(AuthTestWebApplicationFactory.HumanOperatorSubject, null, true, false)]
    [InlineData("console-tenant-admin", "tenant-admins", false, true)]
    [InlineData(AuthTestWebApplicationFactory.HumanOperatorSubject, "tenant-admins", true, true)]
    [InlineData("console-ordinary-user", null, false, false)]
    [InlineData("console-groups-only-operator", "operators", false, false)]
    public async Task AConsoleToken_IsAnsweredWithTheCallersRoles(
        string subject, string? group, bool expectedOperator, bool expectedTenantAdmin)
    {
        var token = group is null ? ConsoleToken(subject) : ConsoleToken(subject, new Claim("groups", group));

        var response = await GetAsync(token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("operator").GetBoolean().Should().Be(expectedOperator);
        body.GetProperty("tenantAdmin").GetBoolean().Should().Be(expectedTenantAdmin);
    }

    [Fact]
    public async Task ACallerWhoseTenantIsNotActive_StillGetsItsRoles()
    {
        // This host's tenant repository knows no tenant, so ActiveTenantEndpointFilter would answer 403.
        var token = ConsoleToken("console-tenant-admin",
            new Claim("groups", "tenant-admins"), new Claim("tenant_id", "tenant_unknown"));

        var response = await GetAsync(token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("tenantAdmin").GetBoolean().Should().BeTrue();
    }
}
