using Iverson.Api.Schema;

namespace Iverson.Api.Grpc;

/// <summary>
/// RegisterSchema's privileged effects (CSR round 12 §5): per descriptor in order, the DDL and the
/// registry write, then the re-render enqueue for each changed template. The api's implementation
/// (<c>QueuedSchemaRegistrationExecutor</c>) queues them for the worker and waits; the worker's
/// (<c>SchemaRegistrationApplier</c>) applies them. Failures are <c>RpcException</c>s carrying the
/// status the caller receives.
/// </summary>
public interface ISchemaRegistrationExecutor
{
    Task ExecuteAsync(
        IReadOnlyList<SchemaDescriptor> descriptors,
        IReadOnlyList<string> changedTemplateTypes,
        CancellationToken ct);
}
