using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace Iverson.Events;

public class KafkaBrokerHealthCheck(
    IAdminClient adminClient,
    ILogger<KafkaBrokerHealthCheck> logger) : IEventBrokerHealthCheck
{
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// <c>IAdminClient.GetMetadata</c> is the SDK's synchronous call, so it is offloaded to a
    /// thread-pool thread rather than blocking the caller's async context. It queries broker
    /// metadata only — no topic is created, no message is produced or consumed.
    /// </summary>
    public Task<bool> PingAsync() => Task.Run(() =>
    {
        try
        {
            adminClient.GetMetadata(MetadataTimeout);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Kafka broker metadata fetch failed");
            return false;
        }
    });
}
