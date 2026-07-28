namespace Iverson.StarRocks;

public sealed class EngagementStoreOptions
{
    public const string Section = "Engagement";

    /// <summary>
    /// When false, the engagement store is not deployed: the consumer is not registered,
    /// search/aggregate fail with FailedPrecondition, and StarRocks is dropped from the
    /// readiness verdict. Defaults true so existing deployments are unaffected.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
