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
    IRowFieldAuthorizationEvaluator authEvaluator)
    : ObjectRetrievalService.ObjectRetrievalServiceBase
{
    public override async Task<RetrievalResponse> Get(
        RetrievalRequest request, ServerCallContext context)
    {
        logger.LogInformation("[Retrieval.Get] type={Type} key={Key}", request.TypeName.SanitizeForLog(), request.Key);

        var schema = registry.Get(request.TypeName);
        if (schema is null)
            return new RetrievalResponse { Found = false, TraceId = request.TraceId };

        var rowJson = await _entities.FetchByKeyAsync(SchemaBuilder.ToTableSchema(schema), request.Key);

        if (rowJson is null)
            return new RetrievalResponse { Found = false, TraceId = request.TraceId };

        var data = JsonParser.Default.Parse<Struct>(rowJson);

        var decision = authEvaluator.Evaluate(schema, actingUserAccessor.ActingUser, AuthorizationAction.Read);
        if (decision.Denied ||
            (decision.OwnershipRequired &&
             StructFieldAccess.GetFieldString(data, decision.OwnerFieldName!) != decision.OwnerValue) ||
            (decision.TenantColumn is not null &&
             StructFieldAccess.GetFieldString(data, decision.TenantColumn) != decision.TenantValue))
        {
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
            foreach (var key in keys)
                await responseStream.WriteAsync(new RetrievalResponse { Found = false, TraceId = request.TraceId });
            return;
        }

        var rows = await _entities.FetchManyByKeysAsync(SchemaBuilder.ToTableSchema(schema), keys);
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
            if ((decision.OwnershipRequired &&
                 StructFieldAccess.GetFieldString(data, decision.OwnerFieldName!) != decision.OwnerValue) ||
                (decision.TenantColumn is not null &&
                 StructFieldAccess.GetFieldString(data, decision.TenantColumn) != decision.TenantValue))
            {
                await responseStream.WriteAsync(new RetrievalResponse { Found = false, TraceId = request.TraceId });
                continue;
            }
            AuthorizationFieldMasking.MaskDisallowedFields(data, decision.AllowedFields);
            await responseStream.WriteAsync(new RetrievalResponse { Found = true, Data = data, TraceId = request.TraceId });
        }
    }
}
