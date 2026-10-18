using System.Net;
using FluentAssertions;
using Iverson.Api.Tests.Helpers;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace Iverson.Api.Tests;

/// <summary>
/// CSR round 12 §7.1, through the real Program.cs: every StarRocks data connection logs in with a
/// password derived from <c>StarRocks:TenantKey</c>, so a process with the engagement store on and
/// no key refuses to start rather than failing each tenant's first read or write. With the store
/// off there is nothing to log in to, and the key is not needed.
/// </summary>
public class StarRocksTenantKeyStartupTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Startup_WithTheEngagementStoreOnAndNoTenantKey_Refuses(string tenantKey)
    {
        using var factory = new AuthTestWebApplicationFactory().WithWebHostBuilder(b =>
        {
            b.UseSetting("Engagement:Enabled", "true");
            b.UseSetting("StarRocks:TenantKey", tenantKey);
        });

        var act = () => factory.CreateClient(); // boots the host, which runs Program.cs's startup block

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("StarRocks:TenantKey is required when Engagement:Enabled is true and was not configured.");
    }

    [Fact]
    public async Task Startup_WithTheEngagementStoreOffAndNoTenantKey_Starts()
    {
        using var factory = new AuthTestWebApplicationFactory().WithWebHostBuilder(b =>
        {
            b.UseSetting("Engagement:Enabled", "false");
            b.UseSetting("StarRocks:TenantKey", "");
        });

        var response = await factory.CreateClient().GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
