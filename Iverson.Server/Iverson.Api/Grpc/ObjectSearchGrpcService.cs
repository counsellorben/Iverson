using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Elasticsearch;
using Iverson.Embeddings;
using Iverson.Vector;
using Microsoft.Extensions.Logging;
using EsAggKind      = Iverson.Elasticsearch.AggregationKind;
using EsAggResult    = Iverson.Elasticsearch.AggregationResult;
using EsAggSpec      = Iverson.Elasticsearch.AggregationSpec;
using EsRangeSpec    = Iverson.Elasticsearch.RangeBucketSpec;
using ProtoAggBucket = Iverson.Client.Contracts.AggregationBucket;
using ProtoAggResult = Iverson.Client.Contracts.AggregationResult;
using ProtoAggSpec   = Iverson.Client.Contracts.AggregationSpec;

namespace Iverson.Api.Grpc;

/// <summary>
/// Three search paths:
///   Search        — ES DSL query (text/scalar/filter clauses).
///   SearchSimilar — Embeds the query text and searches the entity's Qdrant named vector collection.
///   SearchChunks  — Embeds the query text and searches the {collection}_chunks Qdrant collection,
///                   returning passage text for RAG context assembly.
/// </summary>
public sealed class ObjectSearchGrpcService(
    SchemaRegistry registry,
    IElasticsearchService es,
    IVectorService vector,
    IEmbeddingService embedding,
    ILogger<ObjectSearchGrpcService> logger)
    : ObjectSearchService.ObjectSearchServiceBase
{
    // ── ES DSL Search ──────────────────────────────────────────────────────────

    public override async Task Search(
        SearchRequest request,
        IServerStreamWriter<SearchResponse> responseStream,
        ServerCallContext context)
    {
        var schema = RequireSchema(request.TypeName);

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("[Search] type={Type} clauses={Clauses} page={Page}/{Size}",
                request.TypeName, request.Query?.Clauses.Count ?? 0, request.Page, request.PageSize);

        // Build a query string from CONTAINS / EQUALS clauses; route VECTOR_SIMILAR to SearchSimilar.
        var queryText = BuildQueryText(request.Query);

        var docs = await es.SearchAsync<Dictionary<string, object?>>(schema.IndexName, queryText);

        foreach (var doc in docs)
        {
            await responseStream.WriteAsync(new SearchResponse
            {
                Data    = DictToProtoStruct(doc),
                Score   = 1.0f,
                TraceId = request.TraceId
            }, context.CancellationToken);
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

        var queryVector = await embedding.EmbedAsync(
            request.Query, vectorDesc.ModelId, context.CancellationToken);

        var vectorName = ToSnakeCase(vectorDesc.PropertyName) + "_vector";
        var topK       = (ulong)Math.Max(1, (int)request.TopK);

        var results = await vector.SearchNamedAsync(schema.CollectionName, vectorName, queryVector, topK);

        foreach (var r in results)
        {
            var protoStruct = new Struct();
            foreach (var kvp in r.Payload)
                protoStruct.Fields[kvp.Key] = Value.ForString(kvp.Value);

            await responseStream.WriteAsync(new SearchResponse
            {
                Data    = protoStruct,
                Score   = (float)r.Score,
                TraceId = request.TraceId
            }, context.CancellationToken);
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

        var queryVector = await embedding.EmbedAsync(
            request.Query, chunkDesc.ModelId, context.CancellationToken);

        var vectorName       = ToSnakeCase(chunkDesc.PropertyName) + "_vector";
        var chunksCollection = schema.CollectionName + "_chunks";
        var topK             = (ulong)Math.Max(1, (int)request.TopK);

        var results = await vector.SearchNamedAsync(chunksCollection, vectorName, queryVector, topK);

        foreach (var r in results)
        {
            r.Payload.TryGetValue("text",      out var chunkText);
            r.Payload.TryGetValue("parent_id", out var parentId);

            await responseStream.WriteAsync(new ChunkSearchResponse
            {
                ParentKey = parentId  ?? string.Empty,
                ChunkText = chunkText ?? string.Empty,
                Score     = (float)r.Score,
                TraceId   = request.TraceId
            }, context.CancellationToken);
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

        var queryText = BuildQueryText(request.Query);
        var specs     = request.Aggregations.Select(ProtoToEsSpec).ToList();

        var results = await es.AggregateAsync(schema.IndexName, queryText, specs);

        var response = new AggregateResponse { TraceId = request.TraceId };
        foreach (var r in results)
            response.Results.Add(EsResultToProto(r));

        return response;
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private SchemaDescriptor RequireSchema(string typeName) =>
        registry.Get(typeName) ?? throw new RpcException(new Status(StatusCode.FailedPrecondition,
            $"No schema registered for '{typeName}'. Call RegisterSchema first."));

    // Extracts a query string from text-oriented clauses; ignores VECTOR_SIMILAR
    // (those belong in SearchSimilar, not here).
    private static EsAggSpec ProtoToEsSpec(ProtoAggSpec proto) =>
        new(
            Name:             proto.Name,
            Kind:             ProtoKindToEs(proto.Type),
            Field:            proto.Field,
            Size:             proto.Size > 0 ? proto.Size : 10,
            CalendarInterval: string.IsNullOrEmpty(proto.CalendarInterval) ? null : proto.CalendarInterval,
            TimeZone:         string.IsNullOrEmpty(proto.TimeZone)         ? null : proto.TimeZone,
            RangeBuckets:     proto.RangeBuckets.Count > 0
                ? proto.RangeBuckets
                    .Select(b => new EsRangeSpec(b.Key, b.From, b.To))
                    .ToList()
                : null);

    private static ProtoAggResult EsResultToProto(EsAggResult result)
    {
        var proto = new ProtoAggResult
        {
            Name        = result.Name,
            Type        = EsKindToProto(result.Kind),
            MetricValue = result.MetricValue ?? 0.0
        };

        if (result.Buckets is not null)
            foreach (var b in result.Buckets)
                proto.Buckets.Add(new ProtoAggBucket { Key = b.Key, DocCount = b.DocCount });

        return proto;
    }

    private static EsAggKind ProtoKindToEs(AggregationType type) => type switch
    {
        AggregationType.Terms         => EsAggKind.Terms,
        AggregationType.DateHistogram => EsAggKind.DateHistogram,
        AggregationType.Range         => EsAggKind.Range,
        AggregationType.Avg           => EsAggKind.Avg,
        AggregationType.Sum           => EsAggKind.Sum,
        AggregationType.Min           => EsAggKind.Min,
        AggregationType.Max           => EsAggKind.Max,
        AggregationType.Cardinality   => EsAggKind.Cardinality,
        _                             => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    private static AggregationType EsKindToProto(EsAggKind kind) => kind switch
    {
        EsAggKind.Terms         => AggregationType.Terms,
        EsAggKind.DateHistogram => AggregationType.DateHistogram,
        EsAggKind.Range         => AggregationType.Range,
        EsAggKind.Avg           => AggregationType.Avg,
        EsAggKind.Sum           => AggregationType.Sum,
        EsAggKind.Min           => AggregationType.Min,
        EsAggKind.Max           => AggregationType.Max,
        EsAggKind.Cardinality   => AggregationType.Cardinality,
        _                       => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static string BuildQueryText(SearchQuery? query)
    {
        if (query is null || query.Clauses.Count == 0) return string.Empty;

        var textParts = query.Clauses
            .Where(c => c.Operator is SearchOperator.Contains or SearchOperator.Equals
                                   or SearchOperator.StartsWith)
            .Select(c => c.Value?.StringVal)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();

        return string.Join(" ", textParts!);
    }

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
        JsonElement je => JsonElementToProtoValue(je),
        _              => Value.ForString(v.ToString()!)
    };

    private static Value JsonElementToProtoValue(JsonElement je) => je.ValueKind switch
    {
        JsonValueKind.String  => Value.ForString(je.GetString()!),
        JsonValueKind.Number  => Value.ForNumber(je.GetDouble()),
        JsonValueKind.True    => Value.ForBool(true),
        JsonValueKind.False   => Value.ForBool(false),
        JsonValueKind.Null    => Value.ForNull(),
        JsonValueKind.Array   => Value.ForList(je.EnumerateArray()
                                    .Select(JsonElementToProtoValue).ToArray()),
        _                     => Value.ForString(je.ToString())
    };

    private static string ToSnakeCase(string name)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }
}
