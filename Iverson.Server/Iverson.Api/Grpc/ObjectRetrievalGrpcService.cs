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
        RetrievalRequest request, ServerCallContext context)
    {
        logger.LogInformation("[Retrieval.Get] type={Type} key={Key}", request.TypeName.SanitizeForLog(), request.Key);

        var schema = registry.Get(request.TypeName);
        if (schema is null)
            return new RetrievalResponse { Found = false, TraceId = request.TraceId };

        var rowJson = await _entities.FetchByKeyAsync(
            SchemaBuilder.ToTableSchema(schema), request.Key,
            tenantScoped: schema.TenantColumn is not null,
            tenantId: actingUserAccessor.ActingUser?.FindFirst("tenant_id")?.Value);

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
            auditLog.Denied(actingUserAccessor.ActingUser, "Read", request.TypeName, request.Key,
                decision.Denied ? "AccessDenied" : ownerMismatch ? "OwnerMismatch" : "TenantMismatch");
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

        var schema = registry.Get(request.TypeName);

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

        var rows = await _entities.FetchManyByKeysAsync(
            SchemaBuilder.ToTableSchema(schema), keys,
            tenantScoped: decision.TenantColumn is not null,
            tenantId: decision.TenantValue);
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
                auditLog.Denied(actingUserAccessor.ActingUser, "Read", request.TypeName, key,
                    ownerMismatch ? "OwnerMismatch" : "TenantMismatch");
                await responseStream.WriteAsync(new RetrievalResponse { Found = false, TraceId = request.TraceId });
                continue;
            }
            AuthorizationFieldMasking.MaskDisallowedFields(data, decision.AllowedFields);
            await responseStream.WriteAsync(new RetrievalResponse { Found = true, Data = data, TraceId = request.TraceId });
        }
    }
}
