using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using FluentAssertions;
using Iverson.Api.Tests.Helpers;
using Iverson.Events;
using Iverson.Sql;
using Iverson.StarRocks;
using Iverson.Vector;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
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
    private readonly AuthTestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AuthenticationPipelineTests(AuthTestWebApplicationFactory factory)
    {
        _factory = factory;
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
    public async Task ServiceTokenOnly_PostAdminReconcile_Returns401()
    {
        var token = TestJwtFactory.CreateToken(
            "test-service-audience", "test-operator", extraClaims: [new Claim("groups", "operators")]);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/reconcile/SomeType");
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

    [Fact]
    public async Task ServiceTokenAndActingUserToken_PostAdminReconcile_Succeeds()
    {
        // Positive-case sibling to ServiceTokenOnly_PostAdminReconcile_Returns401: proves a
        // valid operator acting-user token is NOT rejected by the auth/authz gate (the Finding-1
        // fix added the isOperator check on the ActingUser principal, mirroring /admin/dlq). A
        // 404 for the unregistered "SomeType" schema is the expected outcome past that gate —
        // asserting it (rather than 200) is enough to prove the gate let the request through
        // without exercising the full reconciliation pipeline.
        var serviceToken = TestJwtFactory.CreateToken(
            "test-service-audience", "test-operator", extraClaims: [new Claim("groups", "operators")]);
        var actingUserToken = TestJwtFactory.CreateToken(
            "test-actinguser-audience", "test-user",
            extraClaims: [new Claim("tenant_id", "tenant-a"), new Claim("groups", "operators")]);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/reconcile/SomeType");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);
        request.Headers.Add("x-acting-user-authorization", $"Bearer {actingUserToken}");

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ActingUserWithNoTenantClaimAndNotOperator_GetAdminDlq_ReturnsForbidden()
    {
        // Round-4 whole-branch review finding: `r.TenantId == actingTenantId` is `null == null`
        // (true) for every untenanted row when the acting user's tenant_id claim is absent,
        // bypassing the isOperator gate entirely. An acting user with neither a tenant_id claim
        // nor operator group membership must be rejected outright, not silently handed every
        // untenanted row. This is observable even against the no-op DLQ repository (always empty):
        // before the fix this request returned 200 OK; after the fix, it must never reach the
        // repository at all.
        var serviceToken = TestJwtFactory.CreateToken(
            "test-service-audience", "test-operator", extraClaims: [new Claim("groups", "operators")]);
        var actingUserToken = TestJwtFactory.CreateToken(
            "test-actinguser-audience", "test-user"); // no tenant_id claim, no operator group

        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/dlq");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);
        request.Headers.Add("x-acting-user-authorization", $"Bearer {actingUserToken}");

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public void AdminDlqEndpoint_IsMarkedForDataPlaneListenerPort()
    {
        // Regression test for the RequireHost→listener-port repartitioning (CSR round-8,
        // Finding 1): /admin/dlq must carry the data-plane (8080) marker, not the health
        // listener's. Excludes the replay sibling ("/admin/dlq/{id}/replay") so this asserts
        // against the GET list endpoint specifically. Port == 8080 (not 8081) also doubles as
        // this branch's negative case for the final-review Finding 3 seam test below: it proves
        // an admin endpoint would NOT be excluded by the GlobalLimiter's isHealthListenerEndpoint
        // check, which only matches Port == 8081.
        var dataSource = _factory.Services.GetRequiredService<EndpointDataSource>();
        var dlq = dataSource.Endpoints.Single(e => e.DisplayName!.Contains("/admin/dlq") && !e.DisplayName.Contains("replay"));

        dlq.Metadata.GetMetadata<Program.RequireListenerPort>()!.Port.Should().Be(8080);
    }

    [Theory]
    // Final-review Finding 3 (CSR round-8 whole-branch review): the GlobalLimiter added in
    // Task 3 reads the exact same RequireListenerPort metadata that Task 1's
    // ListenerPortGateAsync gate attaches — the one genuine cross-task dependency between the
    // two tasks. Nothing asserted that every current health-listener-only endpoint still
    // carries the Port == 8081 marker; if a future endpoint silently lost it, the GlobalLimiter
    // would start rate-limiting kubelet's health probe / Prometheus scraping instead of
    // excluding it, and the pod would get pulled out of service under load. This locks down
    // marker-completeness: a future endpoint added without the marker fails this assertion in
    // CI rather than surfacing as a 429 in production.
    [InlineData("/health/live")]
    [InlineData("/build")]
    [InlineData("/health")]
    [InlineData("/metrics")]
    public void HealthListenerEndpoint_IsMarkedForHealthListenerPort(string routePattern)
    {
        var dataSource = _factory.Services.GetRequiredService<EndpointDataSource>();
        var endpoint = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == routePattern);

        endpoint.Metadata.GetMetadata<Program.RequireListenerPort>()!.Port.Should().Be(8081);
    }

    [Fact]
    public async Task ListenerPortGate_WrongPort_Returns404AndDoesNotInvokeNext()
    {
        var context = new DefaultHttpContext { Connection = { LocalPort = 8081 } };
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new Program.RequireListenerPort(8080)),
            "test"));
        var nextInvoked = false;
        Task Next() { nextInvoked = true; return Task.CompletedTask; }

        await Program.ListenerPortGateAsync(context, Next);

        nextInvoked.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task ListenerPortGate_MatchingPort_InvokesNext()
    {
        var context = new DefaultHttpContext { Connection = { LocalPort = 8080 } };
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new Program.RequireListenerPort(8080)),
            "test"));
        var nextInvoked = false;
        Task Next() { nextInvoked = true; return Task.CompletedTask; }

        await Program.ListenerPortGateAsync(context, Next);

        nextInvoked.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task ListenerPortGate_LocalPortZero_InvokesNext()
    {
        // TestServer carve-out: WebApplicationFactory's in-memory TestServer reports
        // Connection.LocalPort == 0 (no real bound socket), so the gate must not 404 it.
        var context = new DefaultHttpContext { Connection = { LocalPort = 0 } };
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new Program.RequireListenerPort(8080)),
            "test"));
        var nextInvoked = false;
        Task Next() { nextInvoked = true; return Task.CompletedTask; }

        await Program.ListenerPortGateAsync(context, Next);

        nextInvoked.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task ListenerPortGate_UnmarkedEndpoint_InvokesNext()
    {
        var context = new DefaultHttpContext { Connection = { LocalPort = 8081 } };
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(),
            "test"));
        var nextInvoked = false;
        Task Next() { nextInvoked = true; return Task.CompletedTask; }

        await Program.ListenerPortGateAsync(context, Next);

        nextInvoked.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    // AuthTestWebApplicationFactory's own ConfigureWebHost substitutes the 7 startup-blocking
    // interfaces (embedding init, schema registry, etc.) but leaves the 4 dependencies /health
    // reads (IRecordStoreQueryExecutor, IEngagementStoreHealthCheck, IVectorSchemaManager,
    // IEventBrokerHealthCheck) wired to their real registrations — verified directly against
    // that file before writing this test. A bare WebApplicationFactory<Program> can't be used
    // here instead: Program.cs's post-Build() block runs several unconditional awaits against
    // real Postgres/schema infra before app.Run(), and only AuthTestWebApplicationFactory's own
    // substitutions let the host reach that point at all. So this factory derives from
    // AuthTestWebApplicationFactory (calling its ConfigureWebHost first) and layers the 4
    // /health-specific substitutions on top, mirroring that base class's own RemoveAll/
    // AddSingleton pattern.
    private sealed class HealthCacheTestFactory : AuthTestWebApplicationFactory
    {
        public readonly IRecordStoreQueryExecutor Db = Substitute.For<IRecordStoreQueryExecutor>();
        public readonly IEngagementStoreHealthCheck StarRocks = Substitute.For<IEngagementStoreHealthCheck>();
        public readonly IVectorSchemaManager Vector = Substitute.For<IVectorSchemaManager>();
        public readonly IEventBrokerHealthCheck Kafka = Substitute.For<IEventBrokerHealthCheck>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IRecordStoreQueryExecutor>();
                services.AddSingleton(Db);

                services.RemoveAll<IEngagementStoreHealthCheck>();
                services.AddSingleton(StarRocks);

                services.RemoveAll<IVectorSchemaManager>();
                services.AddSingleton(Vector);

                services.RemoveAll<IEventBrokerHealthCheck>();
                services.AddSingleton(Kafka);
            });
        }
    }

    [Fact]
    public async Task GetHealth_TwoRequestsWithinCacheWindow_OnlyInvokesDependenciesOnce()
    {
        // CSR remediation task 3: /health's composite result is cached for 2 seconds so an
        // anonymous, unauthenticated endpoint reachable by anything on the health-listener port
        // can't be used to force a check storm against Postgres/StarRocks/Qdrant/Kafka on every
        // request. Two sequential calls inside the cache window must hit each dependency once.
        using var factory = new HealthCacheTestFactory();
        factory.Db.QuerySingleOrDefaultAsync<int>(Arg.Any<string>()).Returns(1);
        factory.StarRocks.CheckHealthAsync().Returns(EngagementHealthStatus.Healthy);
        factory.Vector.PingAsync().Returns(true);
        factory.Kafka.PingAsync().Returns(true);

        var client = factory.CreateClient();

        var first = await client.GetAsync("/health");
        var second = await client.GetAsync("/health");

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        await factory.Db.Received(1).QuerySingleOrDefaultAsync<int>(Arg.Any<string>());
        await factory.StarRocks.Received(1).CheckHealthAsync();
        await factory.Vector.Received(1).PingAsync();
        await factory.Kafka.Received(1).PingAsync();
    }
}
