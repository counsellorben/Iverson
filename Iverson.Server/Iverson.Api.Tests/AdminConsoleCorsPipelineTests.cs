using System;
using System.Linq;
using System.Net;
using FluentAssertions;
using Iverson.Api.Tests.Helpers;
using Xunit;

namespace Iverson.Api.Tests;

// Task 4's CORS registration (Program.cs, "CORS (admin console)") boots the real Program.cs
// pipeline via CorsConfiguredTestWebApplicationFactory / CorsDisabledTestWebApplicationFactory /
// CorsConfiguredAdminListenerTestWebApplicationFactory (all thin AuthTestWebApplicationFactory
// derivations — see Helpers/AdminConsoleCorsTestWebApplicationFactories.cs) so app.UseCors,
// app.UseAuthentication, and the real FallbackPolicy from Program.cs's AddAuthorization block
// all actually run, in the real order Program.cs wires them in.
//
// A standalone throwaway host (copying just the AddCors/UseCors lines, with no real
// FallbackPolicy) verified the CORS *policy semantics* during this task's first review round,
// but could not verify that ordering claim against the real FallbackPolicy — it had no
// FallbackPolicy to get the ordering wrong against. These tests close that gap. They also fix
// the review's other finding against that earlier substitute: it hardcoded an origin
// (http://localhost:5173) that appears in no configuration file in this repo. Here, the
// origin lives in exactly one place (CorsConfiguredTestWebApplicationFactory.ConfiguredOrigin)
// and every assertion below reads it from there, on both the request and the expected
// response header, rather than each independently hardcoding a copy of the string.
//
// /health/live, not /health, is used as the anonymous cross-origin target: ProbeAuthorization
// PipelineTests.cs already documents why calling /health for real against this test host
// (which has no real Postgres/StarRocks/Qdrant/Kafka) hangs rather than failing fast — two of
// its four backend checks are unbounded fan-out calls with no connect timeout in this
// environment. /health/live carries the identical AllowAnonymous + CORS-middleware-ordering
// shape (Program.cs registers UseCors before UseAuthentication, and both endpoints are mapped
// after that, so neither route's own handler participates in the CORS decision at all) without
// that hang risk.
public class AdminConsoleCorsPipelineTests :
    IClassFixture<CorsConfiguredTestWebApplicationFactory>,
    IClassFixture<CorsDisabledTestWebApplicationFactory>,
    IClassFixture<CorsConfiguredAdminListenerTestWebApplicationFactory>
{
    private const string UnlistedOrigin = "https://evil.example";

    private readonly HttpClient _configured;
    private readonly HttpClient _disabled;
    private readonly HttpClient _configuredOnAdminListener;

    public AdminConsoleCorsPipelineTests(
        CorsConfiguredTestWebApplicationFactory configured,
        CorsDisabledTestWebApplicationFactory disabled,
        CorsConfiguredAdminListenerTestWebApplicationFactory configuredOnAdminListener)
    {
        _configured = configured.Client;
        _disabled = disabled.Client;
        _configuredOnAdminListener = configuredOnAdminListener.Client;
    }

    private static HttpRequestMessage CrossOriginGet(string path, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Origin", origin);
        return request;
    }

    // 1. A cross-origin GET from the configured origin gets the matching ACAO header.
    [Fact]
    public async Task ConfiguredOrigin_CrossOriginGet_HealthLive_ReturnsOkWithMatchingAllowOrigin()
    {
        using var request = CrossOriginGet("/health/live", CorsConfiguredTestWebApplicationFactory.ConfiguredOrigin);

        var response = await _configured.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values).Should().BeTrue();
        values!.Should().ContainSingle().Which.Should().Be(CorsConfiguredTestWebApplicationFactory.ConfiguredOrigin);
    }

    // 2. A cross-origin GET from an unlisted origin gets no ACAO header at all — this is not
    // AllowAnyOrigin. (The server still answers the request itself; a real browser is what
    // would refuse to let JS read the response without the header.)
    [Fact]
    public async Task UnlistedOrigin_CrossOriginGet_HealthLive_ReturnsOkWithNoAllowOriginHeader()
    {
        using var request = CrossOriginGet("/health/live", UnlistedOrigin);

        var response = await _configured.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    // 3. The assertion that matters most: a preflight OPTIONS to an authorized /admin/... path
    // is answered by CORS, not rejected by the real FallbackPolicy (Program.cs's
    // RequireAuthenticatedUser). The standalone-host substitute from the first review round
    // could not make this specific claim — it had no FallbackPolicy to get the ordering wrong
    // against. GET /admin/dlq requires RequireAuthorization("Operator"), so if UseCors ran
    // after UseAuthentication/UseAuthorization, this exact preflight would be rejected as
    // unauthenticated before CORS ever got a chance to answer it.
    [Fact]
    public async Task ConfiguredOrigin_PreflightOptions_AdminDlq_AnsweredByCorsNotFallbackPolicy()
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/admin/dlq");
        request.Headers.Add("Origin", CorsConfiguredTestWebApplicationFactory.ConfiguredOrigin);
        request.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await _configured.SendAsync(request);

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowOrigin).Should().BeTrue();
        allowOrigin!.Should().ContainSingle().Which.Should().Be(CorsConfiguredTestWebApplicationFactory.ConfiguredOrigin);
    }

    // The browser's span export is a cross-origin POST to /v1/traces on the admin-api host,
    // which the admin-api Ingress sends to the Http1 listener (8081), so its preflight must be
    // answered by CORS there. A MapPost endpoint does not accept preflights: without the relay's
    // HttpMethodMetadata(acceptCorsPreflight: true) the preflight selects gRPC's
    // unimplemented-service catch-all, which is pinned to 8080, and the listener gate answers
    // 404 (docs/specs/2026-09-23-traces-relay-admin-listener-design.md).
    [Fact]
    public async Task ConfiguredOrigin_PreflightOptions_TracesRelay_OnAdminListener_AnsweredByCors()
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/v1/traces");
        request.Headers.Add("Origin", CorsConfiguredTestWebApplicationFactory.ConfiguredOrigin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");

        var response = await _configuredOnAdminListener.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowOrigin).Should().BeTrue();
        allowOrigin!.Should().ContainSingle().Which.Should().Be(CorsConfiguredTestWebApplicationFactory.ConfiguredOrigin);

        // Content-Type: application/json is not CORS-safelisted, so a real browser also
        // requires the preflight response to grant it (and Authorization) via
        // Access-Control-Allow-Headers, and to grant POST via Access-Control-Allow-Methods --
        // otherwise the browser blocks the actual request even though this OPTIONS succeeded.
        // ASP.NET Core CORS answers each as a single comma-separated header value (observed:
        // "Authorization,Content-Type" and "POST"), so both are split on commas and trimmed
        // before the case-insensitive membership check.
        response.Headers.TryGetValues("Access-Control-Allow-Headers", out var allowHeadersValues).Should().BeTrue();
        var allowedHeaders = allowHeadersValues!.SelectMany(v => v.Split(',')).Select(h => h.Trim());
        allowedHeaders.Should().Contain(h => h.Equals("authorization", StringComparison.OrdinalIgnoreCase));
        allowedHeaders.Should().Contain(h => h.Equals("content-type", StringComparison.OrdinalIgnoreCase));

        response.Headers.TryGetValues("Access-Control-Allow-Methods", out var allowMethodsValues).Should().BeTrue();
        var allowedMethods = allowMethodsValues!.SelectMany(v => v.Split(',')).Select(m => m.Trim());
        allowedMethods.Should().Contain(m => m.Equals("POST", StringComparison.OrdinalIgnoreCase));
    }

    // 4. Fail-closed path, exercised through the real pipeline: with AdminConsole:Origin
    // empty, no CORS policy is registered at all (Program.cs's `if
    // (!string.IsNullOrEmpty(adminConsoleOrigin))` guard around both AddCors and UseCors), so
    // a cross-origin request gets no Access-Control-Allow-Origin header under any Origin.
    [Fact]
    public async Task EmptyConfiguredOrigin_CrossOriginGet_HealthLive_ReturnsOkWithNoAllowOriginHeader()
    {
        using var request = CrossOriginGet("/health/live", CorsConfiguredTestWebApplicationFactory.ConfiguredOrigin);

        var response = await _disabled.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    // 5. AllowCredentials was never called on the CORS policy (Program.cs's comment: the API
    // authenticates via a bearer token, not a cookie) — confirm the response never carries
    // Access-Control-Allow-Credentials, on the same response that DOES carry ACAO.
    [Fact]
    public async Task ConfiguredOrigin_CrossOriginGet_HealthLive_CarriesNoAllowCredentialsHeader()
    {
        using var request = CrossOriginGet("/health/live", CorsConfiguredTestWebApplicationFactory.ConfiguredOrigin);

        var response = await _configured.SendAsync(request);

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeTrue();
        response.Headers.Contains("Access-Control-Allow-Credentials").Should().BeFalse();
    }

    // 6. X-Trace-Id is set on every response (Program.cs's trace-id middleware), but a
    // cross-origin fetch's Response.headers cannot read it unless it is CORS-exposed: only the
    // handful of CORS-safelisted response headers are visible to script by default, and
    // X-Trace-Id is not one of them. Confirms WithExposedHeaders("X-Trace-Id") actually reaches
    // the wire as Access-Control-Expose-Headers on the configured policy.
    [Fact]
    public async Task ConfiguredOrigin_CrossOriginGet_HealthLive_ExposesTraceIdHeader()
    {
        using var request = CrossOriginGet("/health/live", CorsConfiguredTestWebApplicationFactory.ConfiguredOrigin);

        var response = await _configured.SendAsync(request);

        response.Headers.TryGetValues("Access-Control-Expose-Headers", out var exposed).Should().BeTrue();
        exposed!.Should().ContainSingle().Which.Should().Be("X-Trace-Id");
    }
}
