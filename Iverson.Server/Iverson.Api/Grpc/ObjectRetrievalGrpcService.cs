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
        logger.LogInformation("[Retrieval.Get] type={Type} key={Key}", request.TypeName, request.Key);

        var schema = registry.Get(request.TypeName);
        if (schema is null)
            return new RetrievalResponse { Found = false, TraceId = request.TraceId };

        var rowJson = await _entities.FetchByKeyAsync(SchemaBuilder.ToTableSchema(schema), request.Key);

        if (rowJson is null)
            return new RetrievalResponse { Found = false, TraceId = request.TraceId };

        return new RetrievalResponse
        {
            Found   = true,
            Data    = JsonParser.Default.Parse<Struct>(rowJson),
            TraceId = request.TraceId
        };
    }

    public override async Task GetMany(
        RetrievalManyRequest request,
        IServerStreamWriter<RetrievalResponse> responseStream,
        ServerCallContext context)
    {
        logger.LogInformation("[Retrieval.GetMany] type={Type} count={Count}",
            request.TypeName, request.Keys.Count);

        var schema = registry.Get(request.TypeName);

        if (schema is null)
        {
            foreach (var _ in request.Keys)
                await responseStream.WriteAsync(
                    new RetrievalResponse { Found = false, TraceId = request.TraceId });
            return;
        }

        var keys = request.Keys.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var rows = await _entities.FetchManyByKeysAsync(SchemaBuilder.ToTableSchema(schema), keys);

        var rowsByKey = rows.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var key in keys)
        {
            if (context.CancellationToken.IsCancellationRequested) break;
            await responseStream.WriteAsync(
                rowsByKey.TryGetValue(key, out var row)
                    ? new RetrievalResponse
                      {
                          Found   = true,
                          Data    = JsonParser.Default.Parse<Struct>(row.Data),
                          TraceId = request.TraceId
                      }
                    : new RetrievalResponse { Found = false, TraceId = request.TraceId });
        }
    }
}
