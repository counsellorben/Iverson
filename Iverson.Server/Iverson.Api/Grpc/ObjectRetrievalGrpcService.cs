using Grpc.Core;
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
    ILogger<ObjectRetrievalGrpcService> logger)
    : ObjectRetrievalService.ObjectRetrievalServiceBase
{
    public override Task<RetrievalResponse> Get(
        RetrievalRequest request, ServerCallContext context)
    {
        logger.LogInformation("[Retrieval.Get] type={Type} key={Key}", request.TypeName, request.Key);

        // TODO: look up entity schema for request.TypeName, fetch from primary store by key.
        return Task.FromResult(new RetrievalResponse
        {
            Found   = false,
            TraceId = request.TraceId
        });
    }

    public override async Task GetMany(
        RetrievalManyRequest request,
        IServerStreamWriter<RetrievalResponse> responseStream,
        ServerCallContext context)
    {
        logger.LogInformation("[Retrieval.GetMany] type={Type} count={Count}",
            request.TypeName, request.Keys.Count);

        // TODO: batch-fetch from primary store; stream each result as it resolves.
        foreach (var key in request.Keys)
        {
            if (context.CancellationToken.IsCancellationRequested) break;
            await responseStream.WriteAsync(new RetrievalResponse
            {
                Found   = false,
                TraceId = request.TraceId
            });
        }
    }
}
