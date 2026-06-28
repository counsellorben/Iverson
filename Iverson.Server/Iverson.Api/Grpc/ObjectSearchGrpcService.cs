using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Schema;
using Iverson.Api.StarRocks;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.StarRocks;
using Iverson.Vector;

using SrAggKind   = Iverson.StarRocks.AggregationKind;
using SrAggSpec   = Iverson.StarRocks.AggregationDescriptor;
using SrAggResult = Iverson.StarRocks.AggregationResult;
using SrAggBucket = Iverson.StarRocks.AggregationBucket;
using SrRangeSpec = Iverson.StarRocks.RangeBucketDescriptor;
using ProtoAggBucket = Iverson.Client.Contracts.AggregationBucket;
using ProtoAggResult = Iverson.Client.Contracts.AggregationResult;
using ProtoAggSpec   = Iverson.Client.Contracts.AggregationSpec;

namespace Iverson.Api.Grpc;

/// <summary>
/// Three search paths:
///   Search        — StarRocks SQL WHERE query.
///   SearchSimilar — Embeds the query text and searches the entity's Qdrant named vector collection.
///   SearchChunks  — Embeds the query text and searches the {collection}_chunks Qdrant collection.
/// </summary>
public sealed class ObjectSearchGrpcService(
    SchemaRegistry registry,
    IStarRocksRepository sr,
    IVectorService vector,
    IEmbeddingService embedding,
    ILogger<ObjectSearchGrpcService> logger)
    : ObjectSearchService.ObjectSearchServiceBase
{
    // ── SQL Search ─────────────────────────────────────────────────────────────

    public override async Task Search(
        SearchRequest request,
        IServerStreamWriter<SearchResponse> responseStream,
        ServerCallContext context)
    {
        var schema = RequireSchema(request.TypeName);

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("[Search] type={Type} clauses={Clauses} page={Page}/{Size}",
                request.TypeName, request.Query?.Clauses.Count ?? 0, request.Page, request.PageSize);

        var (sql, param) = StarRocksQueryBuilder.BuildSearch(
            schema.TableName, schema, request.Query, request.Page, request.PageSize,
            fields: request.Fields.Count > 0 ? request.Fields : null);

        var rows = await sr.QueryAsync<dynamic>(sql, param);

        foreach (var row in rows)
        {
            var dict = ((IDictionary<string, object>)row)
                .ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
            await responseStream.WriteAsync(
                new SearchResponse
                {
                    Data    = DictToProtoStruct(dict),
                    Score   = 1.0f,
                    TraceId = request.TraceId
                },
                context.CancellationToken);
        }
    }

    // ── Vector Similarity Search ───────────────────────────────────────────────

    public override async Task SearchSimilar(
        SearchSimilarRequest request,
        IServerStreamWriter<SearchResponse> responseStream,
        ServerCallContext context)
    {
        var schema = RequireSchema(request.TypeName);

        var vectorDesc = schema.VectorFields.FirstOrDefault(v =>
            string.Equals(v.PropertyName, request.Property, StringComparison.OrdinalIgnoreCase))
            ?? throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Property '{request.Property}' on '{request.TypeName}' has no [IversonEmbedding] annotation."));

        if (schema.CollectionName is null)
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"Type '{request.TypeName}' has no Qdrant collection."));

        logger.LogInformation("[SearchSimilar] type={Type} property={Prop} topK={K}",
            request.TypeName, request.Property, request.TopK);

        float[] queryVector;
        try
        {
            queryVector = await embedding.EmbedAsync(request.Query, context.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new RpcException(new Status(StatusCode.Unavailable,
                $"Embedding service unavailable: {ex.Message}"));
        }

        var vectorName = vectorDesc.PropertyName.ToSnakeCase() + "_vector";
        var topK       = (ulong)Math.Max(1, (int)request.TopK);
        var results    = await vector.SearchNamedAsync(schema.CollectionName, vectorName, queryVector, topK);

        foreach (var r in results)
        {
            var protoStruct = new Struct();
            foreach (var kvp in r.Payload)
                protoStruct.Fields[kvp.Key] = Value.ForString(kvp.Value);

            await responseStream.WriteAsync(
                new SearchResponse
                {
                    Data    = protoStruct,
                    Score   = (float)r.Score,
                    TraceId = request.TraceId
                },
                context.CancellationToken);
        }
    }

    // ── Chunk / RAG Search ─────────────────────────────────────────────────────

    public override async Task SearchChunks(
        SearchChunksRequest request,
        IServerStreamWriter<ChunkSearchResponse> responseStream,
        ServerCallContext context)
    {
        var schema = RequireSchema(request.TypeName);

        var chunkDesc = schema.ChunkFields.FirstOrDefault(c =>
            string.Equals(c.PropertyName, request.Property, StringComparison.OrdinalIgnoreCase))
            ?? throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Property '{request.Property}' on '{request.TypeName}' has no [IversonChunk] annotation."));

        if (schema.CollectionName is null)
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"Type '{request.TypeName}' has no Qdrant collection."));

        logger.LogInformation("[SearchChunks] type={Type} property={Prop} topK={K}",
            request.TypeName, request.Property, request.TopK);

        float[] queryVector;
        try
        {
            queryVector = await embedding.EmbedAsync(request.Query, context.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new RpcException(new Status(StatusCode.Unavailable,
                $"Embedding service unavailable: {ex.Message}"));
        }

        var vectorName       = chunkDesc.PropertyName.ToSnakeCase() + "_vector";
        var chunksCollection = schema.CollectionName + "_chunks";
        var topK             = (ulong)Math.Max(1, (int)request.TopK);
        var results          = await vector.SearchNamedAsync(chunksCollection, vectorName, queryVector, topK);

        foreach (var r in results)
        {
            r.Payload.TryGetValue("text",      out var chunkText);
            r.Payload.TryGetValue("parent_id", out var parentId);

            await responseStream.WriteAsync(
                new ChunkSearchResponse
                {
                    ParentKey = parentId  ?? string.Empty,
                    ChunkText = chunkText ?? string.Empty,
                    Score     = (float)r.Score,
                    TraceId   = request.TraceId
                },
                context.CancellationToken);
        }
    }

    // ── Aggregation ────────────────────────────────────────────────────────────

    public override async Task<AggregateResponse> Aggregate(
        AggregateRequest request,
        ServerCallContext context)
    {
        var schema = RequireSchema(request.TypeName);

        if (request.Aggregations.Count == 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "At least one aggregation spec is required."));

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("[Aggregate] type={Type} aggs={Count}", request.TypeName, request.Aggregations.Count);

        var response = new AggregateResponse { TraceId = request.TraceId };

        var aggTasks = request.Aggregations
            .Select(spec => RunAggregationAsync(schema, request.Query, ProtoToSrSpec(spec)))
            .ToList();

        var aggResults = await Task.WhenAll(aggTasks);

        foreach (var result in aggResults)
            if (result is not null) response.Results.Add(SrResultToProto(result));

        return response;
    }

    private async Task<SrAggResult?> RunAggregationAsync(
        SchemaDescriptor schema, SearchQuery? query, SrAggSpec spec)
    {
        var (sql, param) = StarRocksQueryBuilder.BuildAggregate(schema.TableName, schema, query, spec);
        var rows = (await sr.QueryAsync<dynamic>(sql, param)).ToList();

        switch (spec.Kind)
        {
            case SrAggKind.Terms:
            case SrAggKind.DateHistogram:
            case SrAggKind.Range:
            {
                var buckets = rows
                    .Select(r => (IDictionary<string, object>)r)
                    .Where(r => r.TryGetValue("bucket_key", out var k) && k is not null)
                    .Select(r => new SrAggBucket(
                        r["bucket_key"]?.ToString() ?? string.Empty,
                        Convert.ToInt64(r["doc_count"])))
                    .ToList();
                return new SrAggResult(spec.Name, spec.Kind, Buckets: buckets);
            }

            default:
            {
                if (rows.Count == 0) return new SrAggResult(spec.Name, spec.Kind, MetricValue: null);
                var row0 = (IDictionary<string, object>)rows[0];
                row0.TryGetValue("metric_val", out var val);
                return new SrAggResult(spec.Name, spec.Kind,
                    MetricValue: val is null ? null : Convert.ToDouble(val));
            }
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private SchemaDescriptor RequireSchema(string typeName) =>
        registry.Get(typeName) ?? throw new RpcException(new Status(StatusCode.FailedPrecondition,
            $"No schema registered for '{typeName}'. Call RegisterSchema first."));

    private static SrAggSpec ProtoToSrSpec(ProtoAggSpec proto) =>
        new(
            Name:             proto.Name,
            Kind:             ProtoKindToSr(proto.Type),
            Field:            proto.Field,
            Size:             proto.Size > 0 ? proto.Size : 10,
            CalendarInterval: string.IsNullOrEmpty(proto.CalendarInterval) ? null : proto.CalendarInterval,
            TimeZone:         string.IsNullOrEmpty(proto.TimeZone)         ? null : proto.TimeZone,
            RangeBuckets:     proto.RangeBuckets.Count > 0
                ? proto.RangeBuckets.Select(b => new SrRangeSpec(b.Key, b.From, b.To)).ToList()
                : null);

    private static SrAggKind ProtoKindToSr(AggregationType type) => type switch
    {
        AggregationType.Terms         => SrAggKind.Terms,
        AggregationType.DateHistogram => SrAggKind.DateHistogram,
        AggregationType.Range         => SrAggKind.Range,
        AggregationType.Avg           => SrAggKind.Avg,
        AggregationType.Sum           => SrAggKind.Sum,
        AggregationType.Min           => SrAggKind.Min,
        AggregationType.Max           => SrAggKind.Max,
        AggregationType.Cardinality   => SrAggKind.Count,
        _                             => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    private static ProtoAggResult SrResultToProto(SrAggResult result)
    {
        var proto = new ProtoAggResult
        {
            Name        = result.Name,
            Type        = SrKindToProto(result.Kind),
            MetricValue = result.MetricValue ?? 0.0
        };
        if (result.Buckets is not null)
            foreach (var b in result.Buckets)
                proto.Buckets.Add(new ProtoAggBucket { Key = b.Key, Count = b.DocCount });
        return proto;
    }

    private static AggregationType SrKindToProto(SrAggKind kind) => kind switch
    {
        SrAggKind.Terms         => AggregationType.Terms,
        SrAggKind.DateHistogram => AggregationType.DateHistogram,
        SrAggKind.Range         => AggregationType.Range,
        SrAggKind.Avg           => AggregationType.Avg,
        SrAggKind.Sum           => AggregationType.Sum,
        SrAggKind.Min           => AggregationType.Min,
        SrAggKind.Max           => AggregationType.Max,
        SrAggKind.Count         => AggregationType.Cardinality,
        _                       => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static Struct DictToProtoStruct(Dictionary<string, object?> doc)
    {
        var s = new Struct();
        foreach (var (k, v) in doc)
            s.Fields[k] = ToProtoValue(v);
        return s;
    }

    private static Value ToProtoValue(object? v) => v switch
    {
        null           => Value.ForNull(),
        string s       => Value.ForString(s),
        bool b         => Value.ForBool(b),
        double d       => Value.ForNumber(d),
        float f        => Value.ForNumber(f),
        int i          => Value.ForNumber(i),
        long l         => Value.ForNumber(l),
        _              => Value.ForString(v.ToString()!)
    };
}
