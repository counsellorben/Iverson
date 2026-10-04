using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using FluentAssertions;
using Iverson.Api.Tenancy;
using Iverson.Api.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Iverson.Api.Tests;

/// <summary>
/// CSR round-10 #14: <c>ActiveTenantEndpointFilter</c> on all four <c>/admin/console/*</c>
/// endpoints, through the real <c>Program.cs</c> pipeline. Every token here carries both the
/// <c>operators</c> group and the reader group, so it clears every endpoint's authorization
/// policy and the filter is the only thing that can answer 403.
/// </summary>
public class AdminConsoleActiveTenantPipelineTests : IClassFixture<AdminConsoleTestWebApplicationFactory>
{
    private const string Tenants    = "/admin/console/tenants";
    private const string Schema     = "/admin/console/schema";
    private const string DataVolume = "/admin/console/data-volume";
    private const string Qdrant     = "/admin/console/qdrant";

    private readonly HttpClient _client;

    public AdminConsoleActiveTenantPipelineTests(AdminConsoleTestWebApplicationFactory factory)
    {
        _client = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITenantStatusCache>();
                services.AddSingleton<ITenantStatusCache, FixedTenantStatusCache>();
            })).CreateClient();
    }

    /// <summary>One tenant per status the filter distinguishes; any other id is unknown (null).</summary>
    private sealed class FixedTenantStatusCache : ITenantStatusCache
    {
        public Task<string?> GetStatusAsync(string tenantId) => Task.FromResult(tenantId switch
        {
            "tenant_active"    => "active",
            "tenant_suspended" => "suspended",
            "tenant_deleted"   => "deleted",
            _                  => (string?)null
        });
    }

    private static string Token(string? tenantId)
    {
        var claims = new List<Claim>
        {
            new("groups", "operators"),
            new("groups", AdminConsoleTestWebApplicationFactory.ReaderGroup)
        };
        if (tenantId is not null)
            claims.Add(new Claim("tenant_id", tenantId));
        return TestJwtFactory.CreateToken("test-service-audience", "console-user", extraClaims: claims);
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    public static TheoryData<string, string> InactiveTenantOnEveryEndpoint()
    {
        var data = new TheoryData<string, string>();
        foreach (var path in new[] { Tenants, Schema, DataVolume, Qdrant })
            foreach (var tenantId in new[] { "tenant_unknown", "tenant_suspended", "tenant_deleted" })
                data.Add(path, tenantId);
        return data;
    }

    [Theory]
    [MemberData(nameof(InactiveTenantOnEveryEndpoint))]
    public async Task TenantNotActive_Returns403(string path, string tenantId)
    {
        var response = await GetAsync(path, Token(tenantId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(Tenants)]
    [InlineData(Schema)]
    [InlineData(DataVolume)]
    [InlineData(Qdrant)]
    public async Task ActiveTenant_Returns200(string path)
    {
        var response = await GetAsync(path, Token("tenant_active"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(Tenants)]
    [InlineData(Schema)]
    [InlineData(DataVolume)]
    [InlineData(Qdrant)]
    public async Task NoTenantIdClaim_Returns200(string path)
    {
        var response = await GetAsync(path, Token(tenantId: null));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(Tenants)]
    [InlineData(Schema)]
    [InlineData(DataVolume)]
    [InlineData(Qdrant)]
    public async Task EmptyTenantIdClaim_Returns200(string path)
    {
        var response = await GetAsync(path, Token(""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
