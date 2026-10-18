using System.Text.Json;
using Grpc.Core;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Sql;

namespace Iverson.Api.PrivilegedRequests;

/// <summary>
/// RegisterSchema's privileged half, from the api (CSR round 12 §5.2): queue the owner-stamped
/// descriptors, wait up to <see cref="PrivilegedRequestWaits.RegisterClaim"/> for the worker to
/// claim them and then up to <see cref="PrivilegedRequestWaits.RegisterRun"/> for the result. On
/// success this process's registry is reloaded before the caller is answered, so the caller's next
/// write on this replica sees the type; other replicas catch up on their 30 s poll.
/// </summary>
internal sealed class QueuedSchemaRegistrationExecutor(
    PrivilegedRequestClient requests,
    SchemaRegistry registry) : ISchemaRegistrationExecutor
{
    private const string NotPickedUp = "The worker did not pick the registration up; retry.";

    public async Task ExecuteAsync(
        IReadOnlyList<SchemaDescriptor> descriptors,
        IReadOnlyList<string> changedTemplateTypes,
        CancellationToken ct)
    {
        var waits = requests.Waits;
        var payload = JsonSerializer.Serialize(
            new RegisterSchemaPayload(descriptors, changedTemplateTypes), SchemaRegistry.JsonOptions);

        // The row expires with the claim wait, so a worker that only gets to it after the api has
        // given up can never run a registration nobody is waiting for.
        var id = await requests.SubmitAsync(
            PrivilegedRequestKinds.RegisterSchema,
            payload,
            requestedBy: descriptors.Select(d => d.OwnerTenantId).FirstOrDefault(),
            expiresAfter: waits.RegisterClaim);

        var request = await requests.WaitWhileAsync(
            id, status => status == PrivilegedRequestStatus.Pending, waits.RegisterClaim, ct);

        if (request.Status == PrivilegedRequestStatus.Pending)
        {
            // Conditional on 'pending': if the worker's claim got there first, this changes nothing
            // and the claimed request is waited on below.
            if (await requests.ExpireIfPendingAsync(id))
                throw new RpcException(new Status(StatusCode.Unavailable, NotPickedUp));
            request = await requests.ReadAsync(id);
        }

        if (request.Status == PrivilegedRequestStatus.Running)
            request = await requests.WaitWhileAsync(
                id, status => status == PrivilegedRequestStatus.Running, waits.RegisterRun, ct);

        switch (request.Status)
        {
            case PrivilegedRequestStatus.Succeeded:
                await registry.LoadAsync(ct);
                return;
            case PrivilegedRequestStatus.Running:
                throw new RpcException(new Status(StatusCode.DeadlineExceeded,
                    "The registration is still being applied. It completes in the worker; registering " +
                    "again once it has is idempotent."));
            case PrivilegedRequestStatus.Failed:
                var result = PrivilegedRequestResult.From(request)!;
                throw new RpcException(new Status(result.Code, result.Message ?? string.Empty));
            default:
                throw new RpcException(new Status(StatusCode.Unavailable, NotPickedUp));
        }
    }
}
