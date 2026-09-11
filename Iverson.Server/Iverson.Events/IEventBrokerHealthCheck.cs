namespace Iverson.Events;

/// <summary>
/// A non-mutating Kafka broker connectivity check.
///
/// <para><b>Why this exists separately from <see cref="IEventProducer"/>.</b> CSR finding #7:
/// the anonymous <c>/health</c> endpoint used to confirm Kafka reachability by calling
/// <c>IEventProducer.ProduceAsync</c> — an actual produce to <c>iverson.health.probe</c>, i.e. a
/// write, reachable by anything that can reach the port and requiring no authentication. This
/// interface's <see cref="PingAsync"/> is backed by <c>IAdminClient.GetMetadata</c>, which reads
/// broker/cluster metadata over the existing connection without producing, consuming, or creating
/// anything — a liveness signal with no side effect for an anonymous endpoint to expose.</para>
/// </summary>
public interface IEventBrokerHealthCheck
{
    Task<bool> PingAsync();
}
