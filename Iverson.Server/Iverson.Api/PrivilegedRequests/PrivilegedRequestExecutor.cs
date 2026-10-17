using System.Text.Json;
using Grpc.Core;
using Iverson.Api.Schema;
using Iverson.Sql;

namespace Iverson.Api.PrivilegedRequests;

public interface IPrivilegedRequestExecutor
{
    /// <summary>Runs one claimed request. Never throws: every outcome is a result to record.</summary>
    Task<PrivilegedRequestResult> RunAsync(PrivilegedRequestRow request, CancellationToken ct);
}

/// <summary>
/// Runs a claimed privileged request in the worker (CSR round 12 §5.1). It first reloads this
/// process's registry, so a type registered moments ago resolves; then each kind takes its own
/// session advisory lock. A refusal is recorded with its status and message; an unexpected
/// exception is recorded as Internal with a generic message, and its details go to the log.
/// </summary>
internal sealed class PrivilegedRequestExecutor(
    SchemaRegistry registry,
    SchemaRegistrationApplier registration,
    IPrivilegedRequestLocks locks,
    ILogger<PrivilegedRequestExecutor> logger) : IPrivilegedRequestExecutor
{
    public async Task<PrivilegedRequestResult> RunAsync(PrivilegedRequestRow request, CancellationToken ct)
    {
        try
        {
            await registry.LoadAsync(ct);

            return request.Kind switch
            {
                PrivilegedRequestKinds.RegisterSchema => await RegisterSchemaAsync(request, ct),
                _ => throw new InvalidOperationException($"Unknown privileged request kind '{request.Kind}'.")
            };
        }
        catch (RpcException ex)
        {
            return new PrivilegedRequestResult(ex.StatusCode, ex.Status.Detail);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new PrivilegedRequestResult(StatusCode.Unavailable,
                "The worker stopped before the request finished; retry.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Privileged request {RequestId} ({Kind}) failed", request.Id, request.Kind);
            return new PrivilegedRequestResult(StatusCode.Internal,
                "The request failed in the worker; the worker log has the details.");
        }
    }

    // One global lock: registrations run one at a time across every worker replica, so one
    // registration's owner read and registry write never interleave with another's.
    private async Task<PrivilegedRequestResult> RegisterSchemaAsync(PrivilegedRequestRow request, CancellationToken ct)
    {
        var payload = Deserialize<RegisterSchemaPayload>(request);

        await using var registrationLock = await locks.AcquireRegistrationAsync(ct);
        await registration.ExecuteAsync(payload.Descriptors, payload.ChangedTemplateTypes, ct);
        return PrivilegedRequestResult.Ok();
    }

    private static T Deserialize<T>(PrivilegedRequestRow request) where T : class =>
        JsonSerializer.Deserialize<T>(request.Payload, SchemaRegistry.JsonOptions)
        ?? throw new InvalidOperationException($"Privileged request {request.Id} has an empty payload.");
}
