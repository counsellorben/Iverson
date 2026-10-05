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

    [Fact]
    public void FromConfiguration_RejectsHopsThatIsNotAnInteger()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RateLimiting:TrustedProxies:Hops"] = "two",
            ["RateLimiting:TrustedProxies:Cidrs:0"] = "10.244.0.0/16",
        }).Build();

        FluentActions.Invoking(() => TrustedProxyOptions.FromConfiguration(cfg))
            .Should().Throw<InvalidOperationException>().WithMessage("*Hops*");
    }

    [Fact]
    public void FromConfiguration_BindsTheConfiguredValues()
    {
        var opts = TrustedProxyOptions.FromConfiguration(Config(2, "130.211.0.0/22", "fd00::/8"));

        opts.Hops.Should().Be(2);
        opts.Cidrs.Should().Equal("130.211.0.0/22", "fd00::/8");
        opts.Networks.Should().HaveCount(2);
    }
}
