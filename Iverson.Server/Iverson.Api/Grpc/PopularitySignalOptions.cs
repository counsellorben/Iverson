using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Iverson.Api.Schema;
using Iverson.StarRocks;

namespace Iverson.Api.Grpc;

public sealed record PopularitySignalEntry(string ParentType, string Relation);

public sealed class PopularitySignalOptions
{
    public const string Section = "PopularitySignal";
    public List<PopularitySignalEntry> Signals { get; set; } = [];
    public double SaturationPoint { get; set; } = 50;
}

public static class PopularitySignalOptionsExtensions
{
    // Binds and validates only the self-contained field (SaturationPoint). The relation entries
    // need SchemaRegistry, which is not populated until after builder.Build() + LoadAsync() —
    // see ValidateAtStartup below, called separately from Program.cs.
    public static IServiceCollection AddPopularitySignalOptions(
        this IServiceCollection services, IConfiguration config)
    {
        var opts = new PopularitySignalOptions();
        config.GetSection(PopularitySignalOptions.Section).Bind(opts);

        if (!double.IsFinite(opts.SaturationPoint) || opts.SaturationPoint <= 0)
            throw new InvalidOperationException(
                $"{PopularitySignalOptions.Section}:SaturationPoint must be finite and greater than " +
                $"zero (was {opts.SaturationPoint}).");

        services.AddSingleton(Options.Create(opts));
        return services;
    }
}

internal static class PopularitySignalValidator
{
    /// <summary>
    /// Called once at startup, after SchemaRegistry.LoadAsync() — the four checks the design
    /// mandates, all fail-fast. A misconfigured entry throws InvalidOperationException.
    /// </summary>
    internal static void ValidateAtStartup(
        PopularitySignalOptions options, SchemaRegistry registry, bool engagementEnabled)
    {
        foreach (var signal in options.Signals)
        {
            if (!engagementEnabled)
                throw new InvalidOperationException(
                    $"{PopularitySignalOptions.Section}: Signals is non-empty but " +
                    $"{EngagementStoreOptions.Section}:Enabled is false — this feature has no " +
                    "runtime degrade path for a disabled engagement store.");

            var parentSchema = registry.Get(signal.ParentType)
                ?? throw new InvalidOperationException(
                    $"{PopularitySignalOptions.Section}: ParentType '{signal.ParentType}' is not a " +
                    "registered schema.");

            if (!StoreTargeting.HasVectorOrChunkFields(parentSchema))
                throw new InvalidOperationException(
                    $"{PopularitySignalOptions.Section}: ParentType '{signal.ParentType}' has no " +
                    "vector or chunk fields, so it has no Qdrant point to ever patch.");

            var relation = parentSchema.Relations.FirstOrDefault(r =>
                string.Equals(r.PropertyName, signal.Relation, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"{PopularitySignalOptions.Section}: '{signal.Relation}' is not a relation on " +
                    $"'{signal.ParentType}'.");

            if (relation.Kind != RelationKind.OneToMany)
                throw new InvalidOperationException(
                    $"{PopularitySignalOptions.Section}: relation '{signal.Relation}' on " +
                    $"'{signal.ParentType}' is {relation.Kind}, not OneToMany — a count is only " +
                    "meaningful for a one-to-many relation.");

            var childSchema = registry.Get(relation.RelatedTypeName)
                ?? throw new InvalidOperationException(
                    $"{PopularitySignalOptions.Section}: related type '{relation.RelatedTypeName}' " +
                    $"(from relation '{signal.Relation}') is not a registered schema.");

            if (!StoreTargeting.IsEngagementEligible(childSchema))
                throw new InvalidOperationException(
                    $"{PopularitySignalOptions.Section}: child type '{relation.RelatedTypeName}' is " +
                    "not StarRocks-eligible (it declares a OneToMany relation of its own), so its " +
                    "count can never be computed.");
        }
    }
}
