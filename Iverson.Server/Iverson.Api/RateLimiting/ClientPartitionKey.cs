using System.Net;

namespace Iverson.Api.RateLimiting;

/// <summary>
/// CSR round-10 #3: the client address the rate limiters partition on. Behind an ingress or load
/// balancer the TCP peer is the proxy, so every client would share one partition. When the peer
/// is a trusted proxy, the client is the X-Forwarded-For entry <see cref="TrustedProxyOptions.Hops"/>
/// positions from the right: the entry the proxy itself wrote, never one the client supplied.
/// Anything else (untrusted peer, no header, too few entries, unparseable entry) keys on the peer.
/// </summary>
public static class ClientPartitionKey
{
    public static string For(HttpContext ctx, TrustedProxyOptions opts)
    {
        var peer = ctx.Connection.RemoteIpAddress;
        if (peer is null)
            return "anon";
        if (peer.IsIPv4MappedToIPv6)
            peer = peer.MapToIPv4();

        if (!opts.Networks.Any(network => network.Contains(peer)))
            return peer.ToString();

        // Multiple X-Forwarded-For header lines arrive joined by commas, the same as one list.
        var entries = ctx.Request.Headers["X-Forwarded-For"].ToString()
            .Split(',', StringSplitOptions.TrimEntries);
        if (entries.Length < opts.Hops || !IPEndPoint.TryParse(entries[^opts.Hops], out var client))
            return peer.ToString();

        var address = client.Address.IsIPv4MappedToIPv6 ? client.Address.MapToIPv4() : client.Address;
        return address.ToString();
    }
}
