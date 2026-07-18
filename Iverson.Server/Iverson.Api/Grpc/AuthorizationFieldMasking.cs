using System.Security.Claims;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Authorization;
using Iverson.Api.Schema;

namespace Iverson.Api.Grpc;

internal static class AuthorizationFieldMasking
{
    /// <summary>
    /// Shared write-path authorization gate for Post/Update on both ObjectMapping and
    /// ObjectPersistence services: evaluates row+field authorization for the acting user,
    /// denies/throws as appropriate, force-sets or validates the owner field, and rejects
    /// any field the caller isn't allowed to write.
    /// </summary>
    /// <param name="existingRowJson">
    /// JSON of the row being written, or null when there is no pre-existing row — either
    /// because this is a pure create (Post) or because Update's key doesn't exist yet (the
    /// upsert will create it). When null, ownership is force-set rather than validated.
    /// </param>
    /// <param name="deniedMessage">
    /// Exception message used both when the caller has no access at all and — for the
    /// existing-row branch — when an ownership mismatch is detected. Callers pass the
    /// create- or update-specific wording ("Not authorized to create/update this entity.").
    /// </param>
    public static void EnforceWriteAuthorization(
        IRowFieldAuthorizationEvaluator authEvaluator,
        ClaimsPrincipal? actingUser,
        SchemaDescriptor schema,
        Struct payload,
        AuthorizationAction action,
        string deniedMessage,
        string? existingRowJson)
    {
        var decision = authEvaluator.Evaluate(schema, actingUser, action);
        if (decision.Denied)
            throw new RpcException(new Status(StatusCode.PermissionDenied, deniedMessage));

        if (existingRowJson is null)
        {
            // No pre-existing row — either a pure create (Post) or an Update whose key
            // doesn't exist yet and will be created by the upsert. Force-set the tenant
            // column unconditionally (tenant is strictly additive — it applies to bypass
            // callers too, unlike ownership below). Force-set the owner field for
            // ownership-required callers; leave it untouched for bypass callers.
            if (decision.TenantColumn is not null)
                payload.Fields[decision.TenantColumn] = Value.ForString(decision.TenantValue!);
            if (decision.OwnershipRequired)
                payload.Fields[decision.OwnerFieldName!] = Value.ForString(decision.OwnerValue!);
        }
        else
        {
            var existingStruct = JsonParser.Default.Parse<Struct>(existingRowJson);

            // Tenant match + immutability are unconditional — they apply even to bypass
            // callers, unlike the ownership check below.
            if (decision.TenantColumn is not null)
            {
                if (StructFieldAccess.GetFieldString(existingStruct, decision.TenantColumn) != decision.TenantValue)
                    throw new RpcException(new Status(StatusCode.PermissionDenied, deniedMessage));
                var attemptedTenant = StructFieldAccess.GetFieldString(payload, decision.TenantColumn);
                if (attemptedTenant is not null && attemptedTenant != decision.TenantValue)
                    throw new RpcException(new Status(StatusCode.PermissionDenied, "Tenant field is immutable."));
            }

            if (decision.OwnershipRequired &&
                StructFieldAccess.GetFieldString(existingStruct, decision.OwnerFieldName!) != decision.OwnerValue)
                throw new RpcException(new Status(StatusCode.PermissionDenied, deniedMessage));

            // The owner field name for immutability purposes is sourced from the schema's
            // declared Authorization.OwnerField, NEVER from decision.OwnerFieldName — the
            // latter is null for bypass callers, who must still be blocked from reassigning
            // ownership of an existing row.
            var ownerFieldName = schema.Authorization?.OwnerField;
            if (!string.IsNullOrEmpty(ownerFieldName))
            {
                var attemptedOwnerValue = StructFieldAccess.GetFieldString(payload, ownerFieldName);
                if (attemptedOwnerValue is not null &&
                    attemptedOwnerValue != StructFieldAccess.GetFieldString(existingStruct, ownerFieldName))
                    throw new RpcException(new Status(StatusCode.PermissionDenied, "Owner field is immutable after creation."));
            }
        }

        RejectDisallowedFields(payload, decision.AllowedFields, exemptField: decision.OwnerFieldName);
    }

    public static void MaskDisallowedFields(Struct payload, IReadOnlySet<string>? allowedFields, string? exemptField = null)
    {
        if (allowedFields is null) return;

        var toRemove = payload.Fields.Keys
            .Where(key => !allowedFields.Contains(StructSerializer.UpperFirst(key)) && StructSerializer.UpperFirst(key) != exemptField)
            .ToList();
        foreach (var key in toRemove)
            payload.Fields.Remove(key);
    }

    public static void RejectDisallowedFields(
        Struct payload, IReadOnlySet<string>? allowedFields, string? exemptField = null)
    {
        if (allowedFields is null) return;

        var disallowed = payload.Fields.Keys
            .Select(StructSerializer.UpperFirst)
            .Where(canonical => !allowedFields.Contains(canonical) && canonical != exemptField)
            .ToList();
        if (disallowed.Count > 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Field(s) not permitted for this caller: {string.Join(", ", disallowed)}"));
    }
}
