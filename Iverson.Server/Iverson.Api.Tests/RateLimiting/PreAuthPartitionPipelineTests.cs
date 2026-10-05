using System.Net;
using FluentAssertions;
using Iverson.Api.Tests.Helpers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iverson.Api.Tests.RateLimiting;

// Runs on its own, after the parallel collections: its 50,001 requests must land inside the
// limiter's one-minute window, which they do not reliably do while other test classes share the CPU.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PreAuthPartitionPipelineCollection
{
    public const string Name = "pre-auth-partition-pipeline";
}

[Collection(PreAuthPartitionPipelineCollection.Name)]
public class PreAuthPartitionPipelineTests(AuthTestWebApplicationFactory baseFactory) : IClassFixture<AuthTestWebApplicationFactory>
{
    // TestServer supplies no peer address; every request arrives from one trusted ingress pod.
    private sealed class TrustedPeer : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, n) => { ctx.Connection.RemoteIpAddress = IPAddress.Parse("10.244.3.4"); return n(); });
            next(app);
        };
    }

    [Fact]
    public async Task TwoClientsBehindOneTrustedProxy_GetSeparatePreAuthPartitions()
    {
        using var factory = baseFactory.WithWebHostBuilder(b => b
            .UseSetting("RateLimiting:TrustedProxies:Cidrs:0", "10.244.0.0/16")
            .ConfigureServices(s => s.AddTransient<IStartupFilter, TrustedPeer>()));
        var client = factory.CreateClient();

        async Task<HttpStatusCode> Send(string xff)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/admin/dlq");
            req.Headers.Add("X-Forwarded-For", xff);
            using var res = await client.SendAsync(req);
            return res.StatusCode;
        }

        // One past the 50,000/min pre-auth budget for the first client.
        var gate = new SemaphoreSlim(64);
        var first = await Task.Run(() => Task.WhenAll(Enumerable.Range(0, 50_001).Select(async _ =>
        {
            await gate.WaitAsync();
            try { return await Send("203.0.113.7"); } finally { gate.Release(); }
        })));

        first.Should().Contain(HttpStatusCode.TooManyRequests);
        (await Send("203.0.113.8")).Should().NotBe(HttpStatusCode.TooManyRequests);
    }
}
