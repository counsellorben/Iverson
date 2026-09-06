using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Iverson.Api.Grpc;

public sealed class DecayOptions
{
    public const string Section = "Decay";
    public double HalfLifeDays { get; set; } = 180.0;
}

public static class DecayOptionsExtensions
{
    public static IServiceCollection AddDecayOptions(
        this IServiceCollection services, IConfiguration config)
    {
        var opts = new DecayOptions();
        config.GetSection(DecayOptions.Section).Bind(opts);

        if (!double.IsFinite(opts.HalfLifeDays) || opts.HalfLifeDays <= 0)
            throw new InvalidOperationException(
                $"{DecayOptions.Section}:HalfLifeDays must be finite and greater than zero " +
                $"(was {opts.HalfLifeDays}).");

        services.AddSingleton(Options.Create(opts));
        return services;
    }
}
