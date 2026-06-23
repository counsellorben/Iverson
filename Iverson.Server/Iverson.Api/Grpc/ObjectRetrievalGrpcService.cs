using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Sql;
using Microsoft.Extensions.Logging;

namespace Iverson.Api.Grpc;

/// <summary>
/// Key-based entity fetch. Returns raw field data; the client assembles the
/// object graph locally from its relationship attribute metadata.
/// </summary>
public sealed class ObjectRetrievalGrpcService(
    IPostgresRepository _sql,
    SchemaRegistry registry,
    ILogger<ObjectRetrievalGrpcService> logger)
    : ObjectRetrievalService.ObjectRetrievalServiceBase
{
    public override async Task<RetrievalResponse> Get(
        RetrievalRequest request, ServerCallContext context)
    {
        logger.LogInformation("[Retrieval.Get] type={Type} key={Key}", request.TypeName, request.Key);

        var schema = registry.Get(request.TypeName);
        if (schema is null)
            return new RetrievalResponse { Found = false, TraceId = request.TraceId };

        var rowJson = await _sql.QuerySingleOrDefaultAsync<string>(
            $"SELECT row_to_json(t)::text FROM \"{schema.TableName}\" t WHERE \"{schema.KeyColumn.Name}\" = @Key::uuid",
            new { Key = request.Key });

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

        foreach (var key in request.Keys)
        {
            if (context.CancellationToken.IsCancellationRequested) break;

            if (schema is null)
            {
                await responseStream.WriteAsync(new RetrievalResponse { Found = false, TraceId = request.TraceId });
                continue;
            }

            var rowJson = await _sql.QuerySingleOrDefaultAsync<string>(
                $"SELECT row_to_json(t)::text FROM \"{schema.TableName}\" t WHERE \"{schema.KeyColumn.Name}\" = @Key::uuid",
                new { Key = key });

            await responseStream.WriteAsync(rowJson is null
                ? new RetrievalResponse { Found = false, TraceId = request.TraceId }
                : new RetrievalResponse
                {
                    Found   = true,
                    Data    = JsonParser.Default.Parse<Struct>(rowJson),
                    TraceId = request.TraceId
                });
        }
    }
}
