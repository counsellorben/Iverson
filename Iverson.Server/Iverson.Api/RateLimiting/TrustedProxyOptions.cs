using System.Net;

namespace Iverson.Api.RateLimiting;

/// <summary>
/// CSR round-10 #3: the load-balancer or ingress ranges whose X-Forwarded-For the rate limiters
/// trust, and which entry, counted from the right, holds the client. Empty means the header is
/// never trusted (compose, Development): those clients connect to the API directly.
/// </summary>
public sealed class TrustedProxyOptions
{
    public const string Section = "RateLimiting:TrustedProxies";

    public string[] Cidrs { get; set; } = [];
    public int Hops { get; set; } = 1;

    internal IReadOnlyList<IPNetwork> Networks { get; private set; } = [];

    public static TrustedProxyOptions FromConfiguration(IConfiguration cfg)
    {
        var opts = cfg.GetSection(Section).Get<TrustedProxyOptions>() ?? new TrustedProxyOptions();
        if (opts.Hops < 1)
            throw new InvalidOperationException($"{Section}:Hops must be at least 1, got {opts.Hops}.");

        var networks = new List<IPNetwork>(opts.Cidrs.Length);
        foreach (var cidr in opts.Cidrs)
        {
            if (!IPNetwork.TryParse(cidr, out var network))
                throw new InvalidOperationException($"{Section}:Cidrs contains '{cidr}', which is not a CIDR.");
            networks.Add(network);
        }
        opts.Networks = networks;
        return opts;
    }
}
