using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Iverson.Api.RateLimiting;
using Iverson.Api.Tenancy;
using Iverson.Api.Tests.Helpers;
using Iverson.Api.Tests.RateLimiting;
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
        // of an Operator would have satisfied RequireAuthorization("Operator") and returned 200
        // with no acting-user context at all. The endpoint must also require a
        // separately-authenticated "ActingUser" token before it does anything else.
        var token = TestJwtFactory.CreateToken("test-service-audience", AuthTestWebApplicationFactory.OperatorSubject);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/dlq");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ServiceTokenOnly_PostAdminReconcile_Returns401()
    {
        var token = TestJwtFactory.CreateToken(
            "test-service-audience", AuthTestWebApplicationFactory.OperatorSubject);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/reconcile/SomeType");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ServiceTokenAndActingUserToken_GetAdminDlq_Succeeds()
    {
        // tenant-a must be active: /admin/dlq refuses an acting user whose tenant is not (CSR
        // round 11 #21), and this host's no-op tenant repository knows no tenant at all.
        var tenantStatusCache = Substitute.For<ITenantStatusCache>();
        tenantStatusCache.GetStatusAsync("tenant-a").Returns(Task.FromResult<string?>("active"));
        var client = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ITenantStatusCache>();
            services.AddSingleton(tenantStatusCache);
        })).CreateClient();

        var serviceToken = TestJwtFactory.CreateToken(
            "test-service-audience", AuthTestWebApplicationFactory.OperatorSubject);
        var actingUserToken = TestJwtFactory.CreateToken(
            "test-actinguser-audience", AuthTestWebApplicationFactory.ActingOperatorSubject,
            extraClaims: [new Claim("tenant_id", "tenant-a")]);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/dlq");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);
        request.Headers.Add("x-acting-user-authorization", $"Bearer {actingUserToken}");

        var response = await client.SendAsync(request);

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
        // without exercising the full reconciliation pipeline. The reconcile runs in the worker
        // (CSR round 12 §5.2), which this host does not run, so the request queue is faked to
        // answer as the worker does for a type it does not know.
        var requests = Substitute.For<IPrivilegedRequestRepository>();
        requests.GetAsync(Arg.Any<Guid>()).Returns(new PrivilegedRequestRow(
            Guid.Empty, "ReconcileType", "{}", PrivilegedRequestStatus.Failed,
            new Iverson.Api.PrivilegedRequests.PrivilegedRequestResult(
                global::Grpc.Core.StatusCode.NotFound, "No schema registered for 'SomeType'").ToJson()));
        var client = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPrivilegedRequestRepository>();
            services.AddSingleton(requests);
        })).CreateClient();

        var serviceToken = TestJwtFactory.CreateToken(
            "test-service-audience", AuthTestWebApplicationFactory.OperatorSubject);
        var actingUserToken = TestJwtFactory.CreateToken(
            "test-actinguser-audience", AuthTestWebApplicationFactory.ActingOperatorSubject,
            extraClaims: [new Claim("tenant_id", "tenant-a")]);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/reconcile/SomeType");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);
        request.Headers.Add("x-acting-user-authorization", $"Bearer {actingUserToken}");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ActingUserWithNoTenantClaimAndNotOperator_GetAdminDlq_ReturnsForbidden()
    {
        // Round-4 whole-branch review finding: `r.TenantId == actingTenantId` is `null == null`
        // (true) for every untenanted row when the acting user's tenant_id claim is absent,
        // bypassing the isOperator gate entirely. An acting user with neither a tenant_id claim
        // nor Operator must be rejected outright, not silently handed every
        // untenanted row. This is observable even against the no-op DLQ repository (always empty):
        // before the fix this request returned 200 OK; after the fix, it must never reach the
        // repository at all.
        var serviceToken = TestJwtFactory.CreateToken(
            "test-service-audience", AuthTestWebApplicationFactory.OperatorSubject);
        var actingUserToken = TestJwtFactory.CreateToken(
            "test-actinguser-audience", "test-user"); // no tenant_id claim, not an Operator

        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/dlq");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);
        request.Headers.Add("x-acting-user-authorization", $"Bearer {actingUserToken}");

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── CSR round 12 §2.4: Operator at the three acting-user sites ─────────────────────────
    // /admin/reconcile, /admin/dlq and /admin/dlq/{id}/replay decide Operator for the ACTING user by
    // hand, with the Operator policy's own function. An acting user with no tenant_id gets through only
    // as an Operator, so each site answers 403 for one that is not, and passes one on the operator list.

    public static TheoryData<string, string> ActingUserSites() => new()
    {
        { "GET",  "/admin/dlq" },
        { "POST", "/admin/reconcile/SomeType" },
        { "POST", $"/admin/dlq/{Guid.Empty}/replay" },
    };

    // The reconcile runs in the worker (CSR round 12 §5.2), which this host does not run, so the request
    // queue answers as the worker does for a type it does not know. The DLQ is the base host's empty fake.
    private HttpClient ClientWithAQueueThatKnowsNoType()
    {
        var requests = Substitute.For<IPrivilegedRequestRepository>();
        requests.GetAsync(Arg.Any<Guid>()).Returns(new PrivilegedRequestRow(
            Guid.Empty, "ReconcileType", "{}", PrivilegedRequestStatus.Failed,
            new Iverson.Api.PrivilegedRequests.PrivilegedRequestResult(
                global::Grpc.Core.StatusCode.NotFound, "No schema registered for 'SomeType'").ToJson()));
        return _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPrivilegedRequestRepository>();
            services.AddSingleton(requests);
        })).CreateClient();
    }

    private static HttpRequestMessage ActingUserRequest(string method, string path, string actingSubject, params Claim[] actingClaims)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            TestJwtFactory.CreateToken("test-service-audience", AuthTestWebApplicationFactory.OperatorSubject));
        request.Headers.Add("x-acting-user-authorization",
            $"Bearer {TestJwtFactory.CreateToken("test-actinguser-audience", actingSubject, extraClaims: actingClaims)}");
        return request;
    }

    [Theory]
    [MemberData(nameof(ActingUserSites))]
    public async Task ActingUserOnTheOperatorList_WithNoTenant_IsAnOperator_AtEveryActingUserSite(string method, string path)
    {
        var response = await ClientWithAQueueThatKnowsNoType().SendAsync(
            ActingUserRequest(method, path, AuthTestWebApplicationFactory.ActingOperatorSubject));

        // Past the Operator decision: the DLQ list answers (empty), and the reconcile and the replay reach
        // their lookups, which find nothing.
        response.StatusCode.Should().Be(method == "GET" ? HttpStatusCode.OK : HttpStatusCode.NotFound);
    }

    [Theory]
    [MemberData(nameof(ActingUserSites))]
    public async Task ActingUserInTheOperatorsGroupOnly_IsNotAnOperator_AtEveryActingUserSite(string method, string path)
    {
        var response = await ClientWithAQueueThatKnowsNoType().SendAsync(
            ActingUserRequest(method, path, "test-groups-only-user", new Claim("groups", "operators")));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // CSR round 12 §2.1: a hung Authentik must not hold an admin RPC for HttpClient's 100 s default.
    [Fact]
    public void TheAuthentikAdminClient_TimesOutAfterTenSeconds()
    {
        var clients = _factory.Services.GetRequiredService<IHttpClientFactory>();

        clients.CreateClient(IdpAdminClient.HttpClientName).Timeout.Should().Be(TimeSpan.FromSeconds(10));
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
    [InlineData("/health")]
    public void HealthListenerEndpoint_IsMarkedForHealthListenerPort(string routePattern)
    {
        var dataSource = _factory.Services.GetRequiredService<EndpointDataSource>();
        var endpoint = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == routePattern);

        endpoint.Metadata.GetMetadata<Program.RequireListenerPort>()!.Port.Should().Be(8081);
    }

    // CSR round 12 §3.3 (#6): /metrics and /build moved to the metrics listener, which NetworkPolicy
    // opens to Prometheus alone. The gate must refuse them on 8081 (reachable from the edge) and admit
    // them on 8082, and both global limiters must still exempt them, as they did on 8081.
    [Theory]
    [InlineData("/metrics")]
    [InlineData("/build")]
    public void MetricsListenerEndpoint_IsMarkedForMetricsListenerPort(string routePattern) =>
        EndpointFor(routePattern).Metadata.GetMetadata<Program.RequireListenerPort>()!.Port.Should().Be(8082);

    [Theory]
    [InlineData("/metrics", 8082, true)]
    [InlineData("/build", 8082, true)]
    [InlineData("/metrics", 8081, false)]
    [InlineData("/build", 8081, false)]
    [InlineData("/metrics", 8080, false)]
    public async Task MetricsAndBuild_PassTheListenerPortGateOnlyOnTheMetricsListener(string routePattern, int localPort, bool admitted)
    {
        var context = new DefaultHttpContext { Connection = { LocalPort = localPort } };
        context.SetEndpoint(EndpointFor(routePattern));
        var nextInvoked = false;
        Task Next() { nextInvoked = true; return Task.CompletedTask; }

        await Program.ListenerPortGateAsync(context, Next);

        nextInvoked.Should().Be(admitted);
        context.Response.StatusCode.Should().Be(admitted ? StatusCodes.Status200OK : StatusCodes.Status404NotFound);
    }

    [Theory]
    [InlineData("/metrics")]
    [InlineData("/build")]
    public void MetricsListenerEndpoint_IsExemptFromBothGlobalLimiters(string routePattern)
    {
        var trustedProxies = TrustedProxyOptions.FromConfiguration(TrustedProxyOptionsTests.Config(1));
        var context = new DefaultHttpContext { Connection = { LocalPort = 8082 } };
        context.SetEndpoint(EndpointFor(routePattern));

        Program.PreAuthPartition(context, trustedProxies).PartitionKey.Should().Be("unlimited");
        Program.PostAuthPartition(context, trustedProxies).PartitionKey.Should().Be("unlimited");
    }

    private RouteEndpoint EndpointFor(string routePattern) =>
        _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == routePattern);

    [Theory]
    // The browser posts spans to the admin-api host, which the admin-api Ingress sends to the
    // Http1 listener (8081) -- 8080 is h2c-only, which no browser speaks -- while main's api
    // Ingress still routes /v1/traces to 8080. So the relay must pass the gate on both
    // listeners (docs/specs/2026-09-23-traces-relay-admin-listener-design.md).
    [InlineData(8081)]
    [InlineData(8080)]
    public async Task TracesRelay_PassesTheListenerPortGate(int localPort)
    {
        var dataSource = _factory.Services.GetRequiredService<EndpointDataSource>();
        var relay = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/v1/traces");
        var context = new DefaultHttpContext { Connection = { LocalPort = localPort } };
        context.SetEndpoint(relay);
        var nextInvoked = false;
        Task Next() { nextInvoked = true; return Task.CompletedTask; }

        await Program.ListenerPortGateAsync(context, Next);

        nextInvoked.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
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
        public readonly IRecordStoreSchemaManager SchemaManager = Substitute.For<IRecordStoreSchemaManager>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IRecordStoreSchemaManager>();
                services.AddSingleton(SchemaManager);

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
        // The api login's grants probe (CSR round 12 §6.3), also behind the cache.
        factory.Db.QuerySingleOrDefaultAsync<bool>(Arg.Any<string>()).Returns(true);
        factory.StarRocks.CheckHealthAsync().Returns(EngagementHealthStatus.Healthy);
        factory.Vector.PingAsync().Returns(true);
        factory.Kafka.PingAsync().Returns(true);

        var client = factory.CreateClient();
        // Startup's own runtime-role probe ran when the host booted; count only /health's.
        factory.SchemaManager.ClearReceivedCalls();

        var first = await client.GetAsync("/health");
        var second = await client.GetAsync("/health");

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        await factory.Db.Received(1).QuerySingleOrDefaultAsync<int>(Arg.Any<string>());
        await factory.SchemaManager.Received(1).EnsureCanEnterRuntimeRoleAsync();
        await factory.Db.Received(1).QuerySingleOrDefaultAsync<bool>(Arg.Any<string>());
        await factory.StarRocks.Received(1).CheckHealthAsync();
        await factory.Vector.Received(1).PingAsync();
        await factory.Kafka.Received(1).PingAsync();
    }

    [Fact]
    public async Task GetHealth_StarRocksAuthPending_IsReportedAsFalse()
    {
        using var factory = new HealthCacheTestFactory();
        factory.Db.QuerySingleOrDefaultAsync<int>(Arg.Any<string>()).Returns(1);
        factory.StarRocks.CheckHealthAsync().Returns(EngagementHealthStatus.AuthPending);
        factory.Vector.PingAsync().Returns(true);
        factory.Kafka.PingAsync().Returns(true);

        var response = await factory.CreateClient().GetAsync("/health");

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var hasStarRocks = body.RootElement.GetProperty("checks").TryGetProperty("starrocks", out var starRocks);
        hasStarRocks.Should().BeTrue(
            "main's /health collapses AuthPending into false; if it now emits \"authPending\", the "
            + "follow-up against main has landed — restore the live-path titles of HealthStrip's "
            + "authPending tests and update this assertion");
        starRocks.ValueKind.Should().Be(
            JsonValueKind.False,
            "main's /health collapses AuthPending into false; if it now emits \"authPending\", the "
            + "follow-up against main has landed — restore the live-path titles of HealthStrip's "
            + "authPending tests and update this assertion");
    }
}
