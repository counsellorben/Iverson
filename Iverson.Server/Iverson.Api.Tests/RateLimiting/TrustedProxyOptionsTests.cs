using FluentAssertions;
using Iverson.Api.RateLimiting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Iverson.Api.Tests.RateLimiting;

public class TrustedProxyOptionsTests
{
    internal static IConfiguration Config(int? hops, params string[] cidrs)
    {
        var values = new Dictionary<string, string?>();
        if (hops is not null)
            values["RateLimiting:TrustedProxies:Hops"] = hops.Value.ToString();
        for (var i = 0; i < cidrs.Length; i++)
            values[$"RateLimiting:TrustedProxies:Cidrs:{i}"] = cidrs[i];
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void FromConfiguration_AcceptsAnEmptyList_AndDefaultsHopsToOne()
    {
        var opts = TrustedProxyOptions.FromConfiguration(Config(null));

        opts.Networks.Should().BeEmpty();
        opts.Hops.Should().Be(1);
    }

    [Fact]
    public void FromConfiguration_RejectsAnEntryThatIsNotACidr() =>
        FluentActions.Invoking(() => TrustedProxyOptions.FromConfiguration(Config(1, "10.244.0.0/16", "bogus")))
            .Should().Throw<InvalidOperationException>().WithMessage("*RateLimiting:TrustedProxies:Cidrs*bogus*");

    [Fact]
    public void FromConfiguration_RejectsHopsBelowOne() =>
        FluentActions.Invoking(() => TrustedProxyOptions.FromConfiguration(Config(0, "10.244.0.0/16")))
            .Should().Throw<InvalidOperationException>().WithMessage("*RateLimiting:TrustedProxies:Hops*");
}
