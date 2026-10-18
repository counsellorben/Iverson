using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Authorization;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Sql;

namespace Iverson.Api.Grpc;

/// <summary>
/// Key-based entity fetch. Returns raw field data; the client assembles the
/// object graph locally from its relationship attribute metadata.
/// </summary>
public sealed class ObjectRetrievalGrpcService(
    IEntityRepository _entities,
    SchemaRegistry registry,
    ILogger<ObjectRetrievalGrpcService> logger,
    IActingUserAccessor actingUserAccessor,
    IRowFieldAuthorizationEvaluator authEvaluator,
    AuditLog auditLog)
    : ObjectRetrievalService.ObjectRetrievalServiceBase
{
    public override async Task<RetrievalResponse> Get(
        RetrievalRequest request,
        ServerCallContext context)
    {
        logger.LogInformation("[Retrieval.Get] type={Type} key={Key}", request.TypeName.SanitizeForLog(), request.Key.SanitizeForLog());

        // Another tenant's type takes the unregistered branch (CSR round 11 #20).
        var schema = registry.TryGetVisible(request.TypeName, actingUserAccessor.ActingUser);
        if (schema is null)
            return new RetrievalResponse { Found = false, TraceId = request.TraceId };

        var rowJson = await _entities.FetchByKeyAsync(
            SchemaBuilder.ToTableSchema(schema),
            request.Key,
            EntityAccess.ForTenant(actingUserAccessor.ActingUser?.FindFirst("tenant_id")?.Value));

        if (rowJson is null)
            return new RetrievalResponse { Found = false, TraceId = request.TraceId };

        var data = JsonParser.Default.Parse<Struct>(rowJson);

        var decision = authEvaluator.Evaluate(schema, actingUserAccessor.ActingUser, AuthorizationAction.Read);
        var ownerMismatch  = decision.OwnershipRequired &&
            StructFieldAccess.GetFieldString(data, decision.OwnerFieldName!) != decision.OwnerValue;
        var tenantMismatch = decision.TenantColumn is not null &&
            StructFieldAccess.GetFieldString(data, decision.TenantColumn) != decision.TenantValue;
        if (decision.Denied || ownerMismatch || tenantMismatch)
        {
            auditLog.Denied(
                actingUserAccessor.ActingUser,
                "Read",
                request.TypeName,
                request.Key,
                decision.Denied
                    ? "AccessDenied"
                    : ownerMismatch
                        ? "OwnerMismatch"
                        : "TenantMismatch");
            return new RetrievalResponse { Found = false, TraceId = request.TraceId };
        }

        AuthorizationFieldMasking.MaskDisallowedFields(data, decision.AllowedFields);

        return new RetrievalResponse
        {
            Found   = true,
            Data    = data,
            TraceId = request.TraceId
        };
    }

    public override async Task GetMany(
        RetrievalManyRequest request,
        IServerStreamWriter<RetrievalResponse> responseStream,
        ServerCallContext context)
    {
        logger.LogInformation("[Retrieval.GetMany] type={Type} count={Count}",
            request.TypeName.SanitizeForLog(), request.Keys.Count);

        // Another tenant's type takes the unregistered branch exactly, ahead of Distinct and the
        // GUID check: one Found=false per raw key (CSR round 11 #20).
        var schema = registry.TryGetVisible(request.TypeName, actingUserAccessor.ActingUser);

        if (schema is null)
        {
            foreach (var _ in request.Keys)
                await responseStream.WriteAsync(
                    new RetrievalResponse { Found = false, TraceId = request.TraceId });
            return;
        }

        var decision = authEvaluator.Evaluate(schema, actingUserAccessor.ActingUser, AuthorizationAction.Read);
        var keys = request.Keys.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        if (decision.Denied)
        {
            auditLog.Denied(actingUserAccessor.ActingUser, "Read", request.TypeName, null, "AccessDenied");
            foreach (var key in keys)
                await responseStream.WriteAsync(new RetrievalResponse { Found = false, TraceId = request.TraceId });
            return;
        }

        foreach (var key in keys)
        {
            if (!Guid.TryParse(key, out _))
                throw new RpcException(new Status(StatusCode.InvalidArgument, $"Key '{key}' is not a valid GUID."));
        }

        // CSR round 12 §6.4. Every loaded descriptor has a tenant column, and every non-denied
        // decision carries it, so this never fires. If it ever did, the read is refused rather than
        // run across tenants: the api never enters BYPASSRLS.
        if (decision.TenantColumn is null)
            throw new RpcException(new Status(StatusCode.Internal, "The type declares no tenant boundary."));

        var rows = await _entities.FetchManyByKeysAsync(
            SchemaBuilder.ToTableSchema(schema),
            keys,
            EntityAccess.ForTenant(decision.TenantValue));
        var rowsByKey = rows.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var key in keys)
        {
            if (context.CancellationToken.IsCancellationRequested) break;
            if (!rowsByKey.TryGetValue(key, out var row))
            {
                await responseStream.WriteAsync(new RetrievalResponse { Found = false, TraceId = request.TraceId });
                continue;
            }
            var data = JsonParser.Default.Parse<Struct>(row.Data);
            var ownerMismatch  = decision.OwnershipRequired &&
                StructFieldAccess.GetFieldString(data, decision.OwnerFieldName!) != decision.OwnerValue;
            var tenantMismatch = decision.TenantColumn is not null &&
                StructFieldAccess.GetFieldString(data, decision.TenantColumn) != decision.TenantValue;
            if (ownerMismatch || tenantMismatch)
            {
                auditLog.Denied(
                    actingUserAccessor.ActingUser,
                    "Read",
                    request.TypeName,
                    key,
                    ownerMismatch
                        ? "OwnerMismatch"
                        : "TenantMismatch");
                await responseStream.WriteAsync(new RetrievalResponse { Found = false, TraceId = request.TraceId });
                continue;
            }
            AuthorizationFieldMasking.MaskDisallowedFields(data, decision.AllowedFields);
            await responseStream.WriteAsync(new RetrievalResponse { Found = true, Data = data, TraceId = request.TraceId });
        }
    }
}
