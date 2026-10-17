using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    public double RecencyBoost { get; set; } = 0.0;
    public double RecencyHalfLifeDays { get; set; } = 180.0;
}

public static class PopularitySignalOptionsExtensions
{
    // Binds and validates the self-contained fields (SaturationPoint, RecencyBoost,
    // RecencyHalfLifeDays). The relation entries
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

        if (!double.IsFinite(opts.RecencyBoost) || opts.RecencyBoost < 0 || opts.RecencyBoost > 1000000)
            throw new InvalidOperationException(
                $"{PopularitySignalOptions.Section}:RecencyBoost must be finite and in [0, 1000000] " +
                $"(was {opts.RecencyBoost}). Beyond that, popularity saturates towards a constant for " +
                "almost every document, so a larger value cannot usefully change ranking and only " +
                "risks overflowing the popularity score to NaN.");

        if (!double.IsFinite(opts.RecencyHalfLifeDays) || opts.RecencyHalfLifeDays <= 0 ||
            opts.RecencyHalfLifeDays > 300)
            throw new InvalidOperationException(
                $"{PopularitySignalOptions.Section}:RecencyHalfLifeDays must be finite and in (0, 300] " +
                $"(was {opts.RecencyHalfLifeDays}). The consumer stores 60 monthly buckets; a longer " +
                "half-life would silently lose tail contribution.");

        services.AddSingleton(Options.Create(opts));
        return services;
    }
}

internal static class PopularitySignalValidator
{
    /// <summary>
    /// Called once at startup, after SchemaRegistry.LoadAsync() — a series of fail-fast checks
    /// on each configured entry. A misconfigured entry throws InvalidOperationException, EXCEPT
    /// an unregistered ParentType, which only logs a warning and skips that entry: schemas are
    /// registered at runtime via the RegisterSchema RPC served by this same process, so on a
    /// fresh deployment (or after a Postgres reset) failing fast here would crash-loop both the
    /// api and worker roles with no way to ever register the schema that would clear the error.
    /// The runtime is already fully tolerant of this exact condition — PopularitySignalConsumer
    /// filters on registry.Get(...) is not null, and PopularitySignalReconciliationWorker
    /// returns early — so only this startup check needed to stop being fatal.
    /// </summary>
    internal static void ValidateAtStartup(
        PopularitySignalOptions options, SchemaRegistry registry, bool engagementEnabled, ILogger logger)
    {
        var seenParentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var signal in options.Signals)
        {
            if (!seenParentTypes.Add(signal.ParentType))
                throw new InvalidOperationException(
                    $"{PopularitySignalOptions.Section}: ParentType '{signal.ParentType}' is " +
                    "configured more than once. Only one signal per ParentType is supported — " +
                    "the read side resolves a single signal per type via FirstOrDefault, so a " +
                    "second entry would be written but silently never ranked on.");

            if (!engagementEnabled)
                throw new InvalidOperationException(
                    $"{PopularitySignalOptions.Section}: Signals is non-empty but " +
                    $"{EngagementStoreOptions.Section}:Enabled is false — this feature has no " +
                    "runtime degrade path for a disabled engagement store.");

            var parentSchema = registry.Get(signal.ParentType);
            if (parentSchema is null)
            {
                logger.LogWarning(
                    "{Section}: ParentType '{ParentType}' is not (yet) a registered schema; " +
                    "skipping this signal at startup. It will take effect once the schema is " +
                    "registered — PopularitySignalConsumer and the reconciliation worker both " +
                    "already tolerate an unregistered ParentType at runtime.",
                    PopularitySignalOptions.Section, signal.ParentType);
                continue;
            }

            if (!StoreTargeting.HasVectorOrChunkFields(parentSchema))
                throw new InvalidOperationException(
                    $"{PopularitySignalOptions.Section}: ParentType '{signal.ParentType}' has no " +
                    "vector or chunk fields, so it has no Qdrant point to ever patch.");

            // CSR round 12 #8: the count is written onto the parent's own point under these two keys,
            // and SearchSimilar maps a payload key onto the column whose camelCase name it equals. A
            // parent column with either name would be overwritten by the count and would return it
            // to every reader of that column.
            var countKey = signal.Relation.ToCamelCase() + "Count";
            var collidingColumn = parentSchema.ScalarColumns.Append(parentSchema.KeyColumn).FirstOrDefault(c =>
                string.Equals(c.Name, countKey, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Name, countKey + "Buckets", StringComparison.OrdinalIgnoreCase));
            if (collidingColumn is not null)
                throw new InvalidOperationException(
                    $"{PopularitySignalOptions.Section}: column '{collidingColumn.Name}' on " +
                    $"'{signal.ParentType}' has the name of a payload key the signal for relation " +
                    $"'{signal.Relation}' writes ('{countKey}' or '{countKey}Buckets'). Rename the " +
                    "column or remove the signal.");

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

            // CSR round 12 #8. Not fatal: an ineligible signal is skipped at runtime by the writer and
            // the ranker alike, and re-registering either type can make it eligible.
            if (!PopularitySignalEligibility.IsEligible(parentSchema, relation, childSchema))
                logger.LogWarning(
                    "{Section}: the signal for relation '{Relation}' on '{ParentType}' is not eligible, " +
                    "so it is neither computed nor ranked on. A caller who can read '{Parent}' could not " +
                    "read every '{ChildType}' row it counts: the parent is owner-scoped, or one of its " +
                    "reading roles cannot read the child's rows, foreign key or [IversonPopularitySignal] " +
                    "column.",
                    PopularitySignalOptions.Section, signal.Relation, signal.ParentType, signal.ParentType,
                    relation.RelatedTypeName);

            if (options.RecencyBoost > 0 && childSchema.PopularitySignalColumn is null)
                logger.LogWarning(
                    "{Section}: RecencyBoost is {RecencyBoost} but child type '{ChildType}' (from " +
                    "relation '{Relation}' on '{ParentType}') has no [IversonPopularitySignal] " +
                    "property. The recency term will have no effect until it declares the marker.",
                    PopularitySignalOptions.Section, options.RecencyBoost, relation.RelatedTypeName,
                    signal.Relation, signal.ParentType);
        }
    }
}
