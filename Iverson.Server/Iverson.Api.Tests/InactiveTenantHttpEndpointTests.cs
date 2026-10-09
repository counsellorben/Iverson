using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Iverson.Api.Tenancy;
using Iverson.Api.Tests.Helpers;
using Iverson.Sql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests;

/// <summary>
/// CSR round 11 #21: <c>/v1/traces</c> and <c>/admin/dlq*</c> refuse a caller whose tenant is not
/// active, as the gRPC surfaces and the admin console already do. A caller whose token carries no
/// <c>tenant_id</c> (an operator's) passes. Through the real <c>Program.cs</c> pipeline, with the
/// tenant status cache fixed per tenant id.
/// </summary>
public class InactiveTenantHttpEndpointTests : IClassFixture<AuthTestWebApplicationFactory>
{
    private readonly AuthTestWebApplicationFactory _baseFactory;

    public InactiveTenantHttpEndpointTests(AuthTestWebApplicationFactory factory) => _baseFactory = factory;

    /// <summary>One tenant per status; any other id is unknown (null).</summary>
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

    private sealed class FakeJaegerHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        }
    }

    private (HttpClient Client, FakeJaegerHandler Jaeger, IDlqRepository Dlq) CreateClient()
    {
        var jaeger = new FakeJaegerHandler();
        var dlq = Substitute.For<IDlqRepository>();
        dlq.ListUnreplayedAsync(Arg.Any<int>()).Returns(Enumerable.Empty<DlqRow>());
        dlq.GetUnreplayedByIdAsync(Arg.Any<Guid>()).Returns((DlqReplayRow?)null);

        var factory = _baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ITenantStatusCache>();
            services.AddSingleton<ITenantStatusCache, FixedTenantStatusCache>();
            services.RemoveAll<IDlqRepository>();
            services.AddSingleton(dlq);
            services.AddHttpClient("JaegerOtlpHttp").ConfigurePrimaryHttpMessageHandler(() => jaeger);
        }));
        return (factory.CreateClient(), jaeger, dlq);
    }

    private static Task<HttpResponseMessage> PostTracesAsync(HttpClient client, string? tenantId)
    {
        var claims = new List<Claim>();
        if (tenantId is not null)
            claims.Add(new Claim("tenant_id", tenantId));
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/traces")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("""{"resourceSpans":[]}"""))
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            TestJwtFactory.CreateToken(AuthTestWebApplicationFactory.ConsoleAudience, "traces-tenant-check-user", extraClaims: claims));
        return client.SendAsync(request);
    }

    private static HttpRequestMessage DlqRequest(HttpMethod method, string path, string? actingTenantId)
    {
        var serviceToken = TestJwtFactory.CreateToken(
            "test-service-audience", "test-operator", extraClaims: [new Claim("groups", "operators")]);
        var actingClaims = new List<Claim> { new("groups", "operators") };
        if (actingTenantId is not null)
            actingClaims.Add(new Claim("tenant_id", actingTenantId));
        var actingUserToken = TestJwtFactory.CreateToken("test-actinguser-audience", "test-user", extraClaims: actingClaims);

        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);
        request.Headers.Add("x-acting-user-authorization", $"Bearer {actingUserToken}");
        return request;
    }

    // ── /v1/traces ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("tenant_unknown")]
    [InlineData("tenant_suspended")]
    [InlineData("tenant_deleted")]
    public async Task Traces_ConsoleTokenOfATenantThatIsNotActive_Returns403_AndIsNeverForwarded(string tenantId)
    {
        var (client, jaeger, _) = CreateClient();

        var response = await PostTracesAsync(client, tenantId);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        jaeger.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData("tenant_active")]
    [InlineData(null)]
    public async Task Traces_ConsoleTokenOfAnActiveTenantOrWithNoTenant_IsForwarded(string? tenantId)
    {
        var (client, jaeger, _) = CreateClient();

        var response = await PostTracesAsync(client, tenantId);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        jaeger.CallCount.Should().Be(1);
    }

    // ── /admin/dlq and /admin/dlq/{id}/replay ────────────────────────────────

    [Theory]
    [InlineData("tenant_unknown")]
    [InlineData("tenant_suspended")]
    [InlineData("tenant_deleted")]
    public async Task ListDlq_ActingUserOfATenantThatIsNotActive_Returns403_AndNeverReadsTheQueue(string tenantId)
    {
        var (client, _, dlq) = CreateClient();

        var response = await client.SendAsync(DlqRequest(HttpMethod.Get, "/admin/dlq", tenantId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await dlq.DidNotReceive().ListUnreplayedAsync(Arg.Any<int>());
    }

    [Theory]
    [InlineData("tenant_active")]
    [InlineData(null)]
    public async Task ListDlq_ActingUserOfAnActiveTenantOrWithNoTenant_Returns200(string? tenantId)
    {
        var (client, _, dlq) = CreateClient();

        var response = await client.SendAsync(DlqRequest(HttpMethod.Get, "/admin/dlq", tenantId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await dlq.Received(1).ListUnreplayedAsync(Arg.Any<int>());
    }

    [Theory]
    [InlineData("tenant_unknown")]
    [InlineData("tenant_suspended")]
    [InlineData("tenant_deleted")]
    public async Task ReplayDlq_ActingUserOfATenantThatIsNotActive_Returns403_BeforeTheRowIsRead(string tenantId)
    {
        var (client, _, dlq) = CreateClient();

        var response = await client.SendAsync(
            DlqRequest(HttpMethod.Post, $"/admin/dlq/{Guid.NewGuid()}/replay", tenantId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await dlq.DidNotReceive().GetUnreplayedByIdAsync(Arg.Any<Guid>());
    }

    [Theory]
    [InlineData("tenant_active")]
    [InlineData(null)]
    public async Task ReplayDlq_ActingUserOfAnActiveTenantOrWithNoTenant_ReachesTheRowLookup(string? tenantId)
    {
        var (client, _, dlq) = CreateClient();

        var response = await client.SendAsync(
            DlqRequest(HttpMethod.Post, $"/admin/dlq/{Guid.NewGuid()}/replay", tenantId));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound); // past the gate; the fake has no row
        await dlq.Received(1).GetUnreplayedByIdAsync(Arg.Any<Guid>());
    }
}
