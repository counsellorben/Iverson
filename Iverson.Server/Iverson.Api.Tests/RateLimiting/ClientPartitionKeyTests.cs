using System.Net;
using System.Security.Claims;
using FluentAssertions;
using Iverson.Api.RateLimiting;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Iverson.Api.Tests.RateLimiting;

public class ClientPartitionKeyTests
{
    private static TrustedProxyOptions Opts(int hops, params string[] cidrs) =>
        TrustedProxyOptions.FromConfiguration(TrustedProxyOptionsTests.Config(hops, cidrs));

    private static HttpContext Ctx(string? peer, string? xff)
    {
        var ctx = new DefaultHttpContext();
        if (peer is not null)
            ctx.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        if (xff is not null)
            ctx.Request.Headers["X-Forwarded-For"] = xff;
        return ctx;
    }

    [Theory]
    [InlineData("10.244.3.4", "203.0.113.7", 1, "203.0.113.7")]                // trusted peer, one entry
    [InlineData("10.244.3.4", "198.51.100.9, 203.0.113.7", 1, "203.0.113.7")] // the proxy's own entry wins over a client-supplied one
    [InlineData("10.244.3.4", "203.0.113.7, 34.120.1.1", 2, "203.0.113.7")]   // client second from the right
    [InlineData("10.244.3.4", "203.0.113.7:51234", 1, "203.0.113.7")]         // ip:port
    [InlineData("10.244.3.4", "2001:db8::1", 1, "2001:db8::1")]               // IPv6
    [InlineData("10.244.3.4", "[2001:db8::1]:443", 1, "2001:db8::1")]         // IPv6 with a port
    [InlineData("192.0.2.50", "203.0.113.7", 1, "192.0.2.50")]                // untrusted peer keeps its own address
    [InlineData("10.244.3.4", null, 1, "10.244.3.4")]                         // no header
    [InlineData("10.244.3.4", "203.0.113.7", 2, "10.244.3.4")]                // fewer entries than hops
    [InlineData("10.244.3.4", "not-an-ip", 1, "10.244.3.4")]                  // malformed entry
    [InlineData("::ffff:10.244.3.4", "203.0.113.7", 1, "203.0.113.7")]        // IPv4-mapped peer is matched as IPv4
    [InlineData("::ffff:192.0.2.50", null, 1, "192.0.2.50")]                  // and keyed as IPv4
    public void For_ReturnsTheClientAddress(string peer, string? xff, int hops, string expected) =>
        ClientPartitionKey.For(Ctx(peer, xff), Opts(hops, "10.244.0.0/16")).Should().Be(expected);

    [Fact]
    public void For_ReturnsAnon_WhenThereIsNoPeerAddress() =>
        ClientPartitionKey.For(Ctx(null, "203.0.113.7"), Opts(1, "10.244.0.0/16")).Should().Be("anon");

    [Fact]
    public void For_NeverTrustsTheHeader_WhenNoProxyIsConfigured() =>
        ClientPartitionKey.For(Ctx("10.244.3.4", "203.0.113.7"), Opts(1)).Should().Be("10.244.3.4");

    [Fact]
    public void PreAuthPartition_PutsTwoClientsBehindOneTrustedProxyInDifferentPartitions()
    {
        var opts = Opts(1, "10.244.0.0/16");

        var first  = Program.PreAuthPartition(Ctx("10.244.3.4", "203.0.113.7"), opts).PartitionKey;
        var second = Program.PreAuthPartition(Ctx("10.244.3.4", "203.0.113.8"), opts).PartitionKey;

        first.Should().Be("203.0.113.7");
        second.Should().Be("203.0.113.8");
    }

    [Fact]
    public void PreAuthPartition_KeepsAnUntrustedPeerInItsOwnPartition_WhateverItsHeaderSays()
    {
        var opts = Opts(1, "10.244.0.0/16");

        Program.PreAuthPartition(Ctx("192.0.2.50", "203.0.113.7"), opts).PartitionKey.Should().Be("192.0.2.50");
        Program.PreAuthPartition(Ctx("192.0.2.50", "203.0.113.8"), opts).PartitionKey.Should().Be("192.0.2.50");
    }

    [Fact]
    public void PostAuthPartition_FallsBackToTheClientAddress_WhenThereIsNoSub() =>
        Program.PostAuthPartition(Ctx("10.244.3.4", "203.0.113.7"), Opts(1, "10.244.0.0/16"))
            .PartitionKey.Should().Be("203.0.113.7");

    [Fact]
    public void PostAuthPartition_PrefersTheSubClaim()
    {
        var ctx = Ctx("10.244.3.4", "203.0.113.7");
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-1")], "test"));

        Program.PostAuthPartition(ctx, Opts(1, "10.244.0.0/16")).PartitionKey.Should().Be("user-1");
    }
}
