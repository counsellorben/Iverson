using System.Text.Json;
using Grpc.Core;
using Iverson.Api.Schema;
using Iverson.Sql;

namespace Iverson.Api.PrivilegedRequests;

internal static class PrivilegedRequestKinds
{
    public const string RegisterSchema = "RegisterSchema";
    public const string ReconcileType  = "ReconcileType";
    public const string ReplayDlq      = "ReplayDlq";
}

/// <summary>
/// A refusal both halves of RegisterSchema raise, with one message: the api's pre-check against its
/// cache, and the worker's checks against the stored row.
/// </summary>
internal static class SchemaRegistrationRefusals
{
    public static RpcException OwnedByAnotherTenant(string typeName) =>
        new(new Status(StatusCode.PermissionDenied,
            $"Type '{typeName}' is registered to another tenant and cannot be re-registered here."));
}

/// <summary><c>/admin/reconcile/{typeName}</c>'s request.</summary>
internal sealed record ReconcileTypePayload(string TypeName);

/// <summary><c>/admin/dlq/{id}/replay</c>'s request: the DLQ row's id.</summary>
internal sealed record ReplayDlqPayload(Guid Id);

/// <summary>
/// One RegisterSchema call's privileged half: the owner-stamped descriptors in request order and
/// the type names whose document template changed. Serialised with
/// <see cref="SchemaRegistry.JsonOptions"/>, as <c>_iverson_schema</c> rows are.
/// </summary>
internal sealed record RegisterSchemaPayload(
    IReadOnlyList<SchemaDescriptor> Descriptors,
    IReadOnlyList<string> ChangedTemplateTypes);

/// <summary>
/// What the worker records as a request's ResultJson. <see cref="StatusCode.OK"/> is success; any
/// other code is the status the api answers with, and <see cref="Message"/> its detail.
/// </summary>
public sealed record PrivilegedRequestResult(StatusCode Code, string? Message = null, int? Count = null)
{
    public static PrivilegedRequestResult Ok(int? count = null) => new(StatusCode.OK, null, count);

    public string ToJson() => JsonSerializer.Serialize(this, SchemaRegistry.JsonOptions);

    /// <summary>The recorded result, or null while the request has none.</summary>
    public static PrivilegedRequestResult? From(PrivilegedRequestRow request) =>
        request.ResultJson is null
            ? null
            : JsonSerializer.Deserialize<PrivilegedRequestResult>(request.ResultJson, SchemaRegistry.JsonOptions);
}
