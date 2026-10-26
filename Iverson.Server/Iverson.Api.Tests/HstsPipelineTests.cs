using System.Net;
using FluentAssertions;
using Iverson.Api.Tests.Helpers;
using Xunit;

namespace Iverson.Api.Tests;

// CSR round 12 §4.4: with Hsts:Enabled, which the chart sets where global.externalScheme is https,
// Kestrel answers HTTPS requests with Strict-Transport-Security for one year, subdomains included, no
// preload. Without it there is no header. /health/live is the anonymous target, as in
// AdminConsoleCorsPipelineTests.
public class HstsPipelineTests :
    IClassFixture<HstsEnabledTestWebApplicationFactory>,
    IClassFixture<HstsDisabledTestWebApplicationFactory>
{
    private readonly HttpClient _enabled;
    private readonly HttpClient _disabled;

    public HstsPipelineTests(HstsEnabledTestWebApplicationFactory enabled, HstsDisabledTestWebApplicationFactory disabled)
    {
        _enabled = enabled.Client;
        _disabled = disabled.Client;
    }

    [Fact]
    public async Task Enabled_HttpsResponse_CarriesOneYearIncludeSubDomains()
    {
        var response = await _enabled.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues("Strict-Transport-Security", out var values).Should().BeTrue();
        values!.Should().ContainSingle().Which.Should().Be("max-age=31536000; includeSubDomains");
    }

    [Fact]
    public async Task Disabled_HttpsResponse_CarriesNoStrictTransportSecurity()
    {
        var response = await _disabled.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Strict-Transport-Security").Should().BeFalse();
    }
}
