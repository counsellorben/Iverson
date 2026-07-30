using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Authorization;
using Iverson.Api.Consumers;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.StarRocks;
using Iverson.Vector;
using Qdrant.Client;

using Filter = Qdrant.Client.Grpc.Filter;
using SrAggKind = Iverson.StarRocks.AggregationKind;
using EngagementAggSpec = Iverson.StarRocks.AggregationDescriptor;
using EngagementAggResult = Iverson.StarRocks.AggregationResult;
using EngagementRangeSpec = Iverson.StarRocks.RangeBucketDescriptor;
using ProtoAggBucket = Iverson.Client.Contracts.AggregationBucket;
using ProtoAggResult = Iverson.Client.Contracts.AggregationResult;
using ProtoAggSpec = Iverson.Client.Contracts.AggregationSpec;

namespace Iverson.Api.Grpc;

/// <summary>
/// Three search paths:
///   Search        — StarRocks SQL WHERE query.
///   SearchSimilar — Embeds the query text and searches the entity's Qdrant named vector collection.
///   SearchChunks  — Embeds the query text and searches the {collection}_chunks Qdrant collection.
/// </summary>
public sealed class ObjectSearchGrpcService(
    SchemaRegistry registry,
    IEngagementStoreSearchService search,
    IVectorQueryService vector,
    IEmbeddingService embedding,
    ILogger<ObjectSearchGrpcService> logger,
    IActingUserAccessor actingUserAccessor,
    IRowFieldAuthorizationEvaluator authEvaluator,
    IntelligenceTenantScope tenantScope,
    IResultReranker reranker)
    : ObjectSearchService.ObjectSearchServiceBase
{
    // ── SQL Search ─────────────────────────────────────────────────────────────

    public override async Task Search(
        SearchRequest request,
        IServerStreamWriter<SearchResponse> responseStream,
        ServerCallContext context)
    {
        var schema = RequireSchema(request.TypeName);

        var joinedTypes = request.Joins.SelectMany(j => new[] { j.LeftType, j.RightType });
        var auth = EvaluateAuthorization(schema, joinedTypes);
        if (auth.PrimaryDenied)
            return; // empty stream — StarRocks never queried
        if (auth.DeniedJoinedType is not null)
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"Not authorized to join '{auth.DeniedJoinedType}'."));

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "[Search] type={Type} clauses={Clauses} page={Page}/{Size}",
                request.TypeName.SanitizeForLog(),
                request.Query?.Clauses.Count ?? 0,
                request.Page,
                request.PageSize);

        IEnumerable<dynamic> rows;
        try
        {
            rows = await search.SearchAsync(
                SchemaBuilder.ToEngagementQuerySchema(schema),
                request.Query,
                request.Page,
                request.PageSize,
                fields: request.Fields.Count > 0 ? request.Fields : null,
                joins: request.Joins,
                registry: t => registry.Get(t) is { } d
                    ? SchemaBuilder.ToEngagementQuerySchema(d)
                    : null,
                authz: auth.Constraints);
        }
        catch (EngagementQueryTranslationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (EngagementNotReadyException ex)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, $"StarRocks is not ready: {ex.Message}"));
        }
        catch (EngagementStoreDisabledException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }

        var primaryConstraint = auth.Constraints.TryGetValue(schema.TypeName, out var pc) ? pc : null;

        foreach (var row in rows)
        {
            var dict = ((IDictionary<string, object>)row)
                .ToDictionary(kv => kv.Key, kv => (object?)kv.Value);

            if (primaryConstraint?.AllowedFields is not null)
                foreach (var key in dict.Keys.Where(k => !primaryConstraint.AllowedFields.Contains(k)).ToList())
                    dict.Remove(key);

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

        var decision = authEvaluator.Evaluate(schema, actingUserAccessor.ActingUser, AuthorizationAction.Read);
        if (decision.Denied)
            return; // empty stream — Qdrant never queried

        var vectorDesc = schema.VectorFields.FirstOrDefault(v =>
            string.Equals(v.PropertyName, request.Property, StringComparison.OrdinalIgnoreCase))
            ?? throw new RpcException(
                new Status(
                    StatusCode.InvalidArgument,
                    $"Property '{request.Property}' on '{request.TypeName}' has no [IversonEmbedding] annotation."));

        if (schema.CollectionName is null)
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"Type '{request.TypeName}' has no Qdrant collection."));

        if (decision.AllowedFields is not null && !decision.AllowedFields.Contains(vectorDesc.PropertyName))
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Property '{request.Property}' on '{request.TypeName}' is not authorized for this caller."));

        Filter? filter = null;
        if (request.Filter.Count > 0)
        {
            var camelCased = request.Filter.Select(c =>
            {
                ValidateFilterProperty(schema, c.Property, "SearchSimilar");
                if (decision.AllowedFields is not null && !decision.AllowedFields.Contains(c.Property))
                    throw new RpcException(new Status(StatusCode.InvalidArgument,
                        $"SearchSimilar: filter property '{c.Property}' is not authorized for this caller."));
                return new SearchClause
                {
                    Property   = c.Property.ToCamelCase(),
                    Operator   = c.Operator,
                    Value      = c.Value,
                    ClauseType = c.ClauseType
                };
            }).ToList();

            try
            {
                filter = IntelligenceFilterBuilder.Build(
                    camelCased, request.FilterLogic, "SearchSimilar", TimestampColumns(schema));
            }
            catch (FilterTranslationException ex)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
            }
        }

        filter = IntelligenceFilterBuilder.ApplyOwnership(
            filter,
            decision.OwnershipRequired,
            schema.Authorization?.OwnerField?.ToCamelCase(),
            decision.OwnerValue);

        logger.LogInformation(
            "[SearchSimilar] type={Type} property={Prop} topK={K} filtered={Filtered}",
            request.TypeName.SanitizeForLog(),
            request.Property.SanitizeForLog(),
            request.TopK,
            filter is not null);

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

        var vectorName     = vectorDesc.PropertyName.ToSnakeCase() + "_vector";
        var topK           = (ulong)Math.Max(1, (int)request.TopK);
        var collectionName = tenantScope.ResolveCollectionName(schema.CollectionName, decision.TenantValue, isChunks: false);

        IReadOnlyList<VectorSearchResult> results;
        using (RequestHeaders.Use("api-key", tenantScope.MintScopedApiKey(collectionName, readOnly: true)))
        {
            try
            {
                results = await vector.SearchNamedAsync(collectionName, vectorName, queryVector, topK * OverFetchFactor, filter);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                // Tenant's collection was never created (no writes yet) — treat as empty result
                // set rather than creating a collection just to search it. See design spec
                // §"Reads against a tenant with no collection yet".
                results = [];
            }
        }

        // The centroid signal only exists for a property that is BOTH embedded and chunked —
        // "<property>_centroid" is written on the object collection only for chunk fields. For an
        // embedding-only property there is no such named vector, so skip the fetch entirely and
        // let every candidate's centroid be absent.
        var centroids = EmptyCentroids;
        if (results.Count > 0 &&
            schema.ChunkFields.Any(c => string.Equals(c.PropertyName, vectorDesc.PropertyName, StringComparison.OrdinalIgnoreCase)))
        {
            centroids = await FetchCentroidsAsync(
                collectionName,
                results.Select(r => r.Id).Distinct().ToList(),
                vectorDesc.PropertyName.ToSnakeCase() + "_centroid",
                "SearchSimilar");
        }

        var decayField = DecayFieldResolver.ResolveDecayField(schema, logger);
        var now        = DateTimeOffset.UtcNow;

        var candidates = results.Select(r => new RerankCandidate(
            Id:        r.Id,
            BaseScore: r.Score,
            Centroid:  centroids.TryGetValue(r.Id, out var centroid) ? centroid : null,
            Decay:     DecayFor(r, decayField, now))).ToList();

        var byId = ResultsById(results);

        foreach (var ranked in reranker.Rerank(queryVector, candidates).Take((int)topK))
        {
            if (!byId.TryGetValue(ranked.Id, out var r)) continue;

            var protoStruct = new Struct();
            foreach (var kvp in r.Payload)
                protoStruct.Fields[kvp.Key] = Value.ForString(kvp.Value);

            AuthorizationFieldMasking.MaskDisallowedFields(protoStruct, decision.AllowedFields, exemptField: "Key");

            await responseStream.WriteAsync(
                new SearchResponse
                {
                    Data    = protoStruct,
                    Score   = (float)ranked.FusedScore,
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

        var decision = authEvaluator.Evaluate(schema, actingUserAccessor.ActingUser, AuthorizationAction.Read);
        if (decision.Denied)
            return; // empty stream — Qdrant never queried

        var chunkDesc = schema.ChunkFields.FirstOrDefault(c =>
            string.Equals(c.PropertyName, request.Property, StringComparison.OrdinalIgnoreCase))
            ?? throw new RpcException(
                new Status(
                    StatusCode.InvalidArgument,
                        $"Property '{request.Property}' on '{request.TypeName}' has no [IversonChunk] annotation."));

        if (schema.CollectionName is null)
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"Type '{request.TypeName}' has no Qdrant collection."));

        if (decision.AllowedFields is not null && !decision.AllowedFields.Contains(chunkDesc.PropertyName))
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Property '{request.Property}' on '{request.TypeName}' is not authorized for this caller."));

        Filter? filter;
        try
        {
            filter = BuildChunksFilter(schema, request, decision.AllowedFields);
        }
        catch (FilterTranslationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }

        filter = IntelligenceFilterBuilder.ApplyOwnership(
            filter,
            decision.OwnershipRequired,
            schema.Authorization?.OwnerField?.ToCamelCase(),
            decision.OwnerValue);

        logger.LogInformation(
            "[SearchChunks] type={Type} property={Prop} topK={K} filtered={Filtered}",
            request.TypeName.SanitizeForLog(),
            request.Property.SanitizeForLog(),
            request.TopK,
            filter is not null);

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
        var chunksCollection = tenantScope.ResolveCollectionName(schema.CollectionName, decision.TenantValue, isChunks: true);
        var topK             = (ulong)Math.Max(1, (int)request.TopK);

        IReadOnlyList<VectorSearchResult> results;
        using (RequestHeaders.Use("api-key", tenantScope.MintScopedApiKey(chunksCollection, readOnly: true)))
        {
            try
            {
                results = await vector.SearchNamedAsync(chunksCollection, vectorName, queryVector, topK * OverFetchFactor, filter);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                // Tenant's chunks collection was never created (no writes yet) — treat as empty
                // result set rather than creating a collection just to search it. See design spec
                // §"Reads against a tenant with no collection yet".
                results = [];
            }
        }

        // A chunk's centroid signal is its PARENT object's centroid, which lives on the object
        // collection (not the chunks collection) under "<property>_centroid". Several chunks
        // routinely share one parent, so the retrieve is batched over the DISTINCT parent ids.
        var parentIds = results
            .Select(r => r.Payload.TryGetValue("parent_id", out var p) ? p : null)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => IntelligenceStoreConsumer.KeyToUlong(p!))
            .Distinct()
            .ToList();

        var centroids = parentIds.Count > 0
            ? await FetchCentroidsAsync(
                tenantScope.ResolveCollectionName(schema.CollectionName, decision.TenantValue, isChunks: false),
                parentIds,
                chunkDesc.PropertyName.ToSnakeCase() + "_centroid",
                "SearchChunks")
            : EmptyCentroids;

        var decayField = DecayFieldResolver.ResolveDecayField(schema, logger);
        var now        = DateTimeOffset.UtcNow;

        var candidates = results.Select(r =>
        {
            float[]? centroid = null;
            if (r.Payload.TryGetValue("parent_id", out var parent) && !string.IsNullOrEmpty(parent))
                centroids.TryGetValue(IntelligenceStoreConsumer.KeyToUlong(parent), out centroid);

            return new RerankCandidate(r.Id, r.Score, centroid, DecayFor(r, decayField, now));
        }).ToList();

        var byId = ResultsById(results);

        foreach (var ranked in reranker.Rerank(queryVector, candidates).Take((int)topK))
        {
            if (!byId.TryGetValue(ranked.Id, out var r)) continue;

            r.Payload.TryGetValue("text",      out var chunkText);
            r.Payload.TryGetValue("parent_id", out var parentId);

            await responseStream.WriteAsync(
                new ChunkSearchResponse
                {
                    ParentKey = parentId  ?? string.Empty,
                    ChunkText = chunkText ?? string.Empty,
                    Score     = (float)ranked.FusedScore,
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
            throw new RpcException(
                new Status(
                    StatusCode.InvalidArgument,
                    "At least one aggregation spec is required."));

        // AggregationSpec entries don't carry their own join info — only the request-level
        // Joins does — so authorization is evaluated once for the whole request, same as Search.
        var joinedTypes = request.Joins.SelectMany(j => new[] { j.LeftType, j.RightType });
        var auth = EvaluateAuthorization(schema, joinedTypes);
        if (auth.PrimaryDenied)
            return new AggregateResponse { TraceId = request.TraceId }; // empty Results — StarRocks never queried
        if (auth.DeniedJoinedType is not null)
            throw new RpcException(
                new Status(
                    StatusCode.InvalidArgument,
                    $"Not authorized to join '{auth.DeniedJoinedType}'."));

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "[Aggregate] type={Type} aggs={Count}",
                request.TypeName.SanitizeForLog(),
                request.Aggregations.Count);

        var response = new AggregateResponse { TraceId = request.TraceId };

        var having = request.Having;

        var aggTasks = request.Aggregations
            .Select(spec => RunAggregationAsync(
                schema,
                request.Query,
                ProtoToEngagementSpec(spec),
                having,
                request.Joins,
                auth.Constraints))
            .ToList();

        var aggResults = await Task.WhenAll(aggTasks);

        foreach (var result in aggResults)
            if (result is not null) response.Results.Add(SrResultToProto(result));

        return response;
    }

    private async Task<EngagementAggResult?> RunAggregationAsync(
        SchemaDescriptor schema,
        SearchQuery? query,
        EngagementAggSpec spec,
        SearchQuery? having = null,
        IReadOnlyList<JoinSpec>? joins = null,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
    {
        try
        {
            return await search.AggregateAsync(
                SchemaBuilder.ToEngagementQuerySchema(schema),
                query,
                spec,
                having,
                joins,
                t => registry.Get(t) is { } d ? SchemaBuilder.ToEngagementQuerySchema(d) : null,
                authz);
        }
        catch (EngagementQueryTranslationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (EngagementNotReadyException ex)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, $"StarRocks is not ready: {ex.Message}"));
        }
        catch (EngagementStoreDisabledException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
    }

    // ── Compound GROUP BY ──────────────────────────────────────────────────────

    public override async Task GroupBy(
        GroupByRequest request,
        IServerStreamWriter<SearchResponse> responseStream,
        ServerCallContext context)
    {
        var schema = RequireSchema(request.TypeName);

        var joinedTypes = request.Joins.SelectMany(j => new[] { j.LeftType, j.RightType });
        var auth = EvaluateAuthorization(schema, joinedTypes);
        if (auth.PrimaryDenied)
            return; // empty stream — StarRocks never queried
        if (auth.DeniedJoinedType is not null)
            throw new RpcException(
                new Status(
                    StatusCode.InvalidArgument,
                    $"Not authorized to join '{auth.DeniedJoinedType}'."));

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("[GroupBy] type={Type} keys={Keys} metrics={Metrics}",
                request.TypeName.SanitizeForLog(), request.Keys.Count, request.Metrics.Count);

        IEnumerable<dynamic> rows;
        try
        {
            rows = await search.GroupByAsync(
                SchemaBuilder.ToEngagementQuerySchema(schema),
                request,
                t => registry.Get(t) is { } d
                    ? SchemaBuilder.ToEngagementQuerySchema(d)
                    : null,
                authz: auth.Constraints);
        }
        catch (EngagementQueryTranslationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (EngagementNotReadyException ex)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, $"StarRocks is not ready: {ex.Message}"));
        }
        catch (EngagementStoreDisabledException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }

        foreach (var row in rows)
        {
            var dict = ((IDictionary<string, object>)row)
                .ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
            await responseStream.WriteAsync(
                new SearchResponse
                {
                    Data    = DictToProtoStruct(dict),
                    TraceId = request.TraceId
                },
                context.CancellationToken);
        }
    }

    // ── Pipeline (CTE chains) ──────────────────────────────────────────────────

    public override async Task Pipeline(
        PipelineRequest request,
        IServerStreamWriter<SearchResponse> responseStream,
        ServerCallContext context)
    {
        var schema = RequireSchema(request.TypeName);

        // A PipelineJoin.Source can name either a registered type or a prior step (CTE) — only
        // the former needs an authorization check here; a step name is validated later by
        // StarRocksPipelineBuilder itself. Evaluated once across every step's joins, same as
        // Search/Aggregate/GroupBy evaluate once across their own request-level Joins.
        var joinedTypes = request.Steps
            .SelectMany(s => s.Joins)
            .Select(j => j.Source)
            .Distinct()
            .Where(src => registry.Get(src) is not null);
        var auth = EvaluateAuthorization(schema, joinedTypes);
        if (auth.PrimaryDenied)
            return; // empty stream — StarRocks never queried
        if (auth.DeniedJoinedType is not null)
            throw new RpcException(
                new Status(
                    StatusCode.InvalidArgument,
                    $"Not authorized to join '{auth.DeniedJoinedType}'."));

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "[Pipeline] type={Type} steps={Steps}",
                request.TypeName.SanitizeForLog(),
                request.Steps.Count);

        IEnumerable<dynamic> rows;
        try
        {
            rows = await search.PipelineAsync(
                SchemaBuilder.ToEngagementQuerySchema(schema),
                request,
                t => registry.Get(t) is { } d
                    ? SchemaBuilder.ToEngagementQuerySchema(d)
                    : null,
                authz: auth.Constraints);
        }
        catch (EngagementQueryTranslationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (EngagementNotReadyException ex)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, $"StarRocks is not ready: {ex.Message}"));
        }
        catch (EngagementStoreDisabledException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }

        foreach (var row in rows)
        {
            var dict = ((IDictionary<string, object>)row)
                .ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
            await responseStream.WriteAsync(
                new SearchResponse
                {
                    Data    = DictToProtoStruct(dict),
                    TraceId = request.TraceId
                },
                context.CancellationToken);
        }
    }

    // ── Re-ranking helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Vector search over-fetch multiplier. Re-ranking can only reorder what the ANN search
    /// returned, so both vector RPCs ask Qdrant for 4 × top_k candidates and trim back to
    /// top_k after fusing. Deliberately uncapped: top_k = 1000 fetches 4000.
    /// </summary>
    private const ulong OverFetchFactor = 4;

    private static readonly IReadOnlyDictionary<ulong, float[]> EmptyCentroids =
        new Dictionary<ulong, float[]>();

    /// <summary>
    /// Fetches parent/object centroids under their own scoped api-key. A failure here degrades
    /// the ranking rather than failing the search: every centroid becomes ABSENT (never a
    /// substituted neutral value), leaving raw-cosine ordering.
    /// </summary>
    private async Task<IReadOnlyDictionary<ulong, float[]>> FetchCentroidsAsync(
        string objectCollection, IReadOnlyList<ulong> ids, string centroidVectorName, string rpcName)
    {
        if (ids.Count == 0) return EmptyCentroids;

        try
        {
            using (RequestHeaders.Use("api-key", tenantScope.MintScopedApiKey(objectCollection, readOnly: true)))
                return await vector.RetrieveNamedVectorAsync(objectCollection, ids, centroidVectorName)
                       ?? EmptyCentroids;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "[{Rpc}] centroid retrieve failed (collection={Collection} vector={Vector} ids={Count}); " +
                "re-ranking without the centroid signal.",
                rpcName, objectCollection.SanitizeForLog(), centroidVectorName.SanitizeForLog(), ids.Count);
            return EmptyCentroids;
        }
    }

    /// <summary>
    /// The re-ranker returns ids and fused scores only, so the original search results are
    /// indexed by id to rebuild each response. Qdrant point ids are unique within a search
    /// result set; TryAdd keeps the first if that ever fails to hold.
    /// </summary>
    private static Dictionary<ulong, VectorSearchResult> ResultsById(IReadOnlyList<VectorSearchResult> results)
    {
        var byId = new Dictionary<ulong, VectorSearchResult>(results.Count);
        foreach (var r in results) byId.TryAdd(r.Id, r);
        return byId;
    }

    private static double? DecayFor(VectorSearchResult result, string? decayField, DateTimeOffset now) =>
        decayField is not null && result.Payload.TryGetValue(decayField, out var stored)
            ? DecayFieldResolver.ComputeDecay(stored, now)
            : null;

    // ── Helpers ────────────────────────────────────────────────────────────────

    private SchemaDescriptor RequireSchema(string typeName) =>
        registry.Get(typeName) ?? throw new RpcException(
            new Status(
                StatusCode.FailedPrecondition,
                $"No schema registered for '{typeName}'. Call RegisterSchema first."));

    private sealed record AuthzResult(
        bool PrimaryDenied,
        string? DeniedJoinedType,
        IReadOnlyDictionary<string, AuthorizationConstraint> Constraints);

    private AuthzResult EvaluateAuthorization(SchemaDescriptor primary, IEnumerable<string> joinedTypeNames)
    {
        var constraints = new Dictionary<string, AuthorizationConstraint>(StringComparer.OrdinalIgnoreCase);

        var primaryDecision = authEvaluator.Evaluate(primary, actingUserAccessor.ActingUser, AuthorizationAction.Read);
        if (primaryDecision.Denied)
            return new AuthzResult(true, null, constraints);
        constraints[primary.TypeName] = new AuthorizationConstraint(
            primaryDecision.AllowedFields, primaryDecision.OwnerFieldName, primaryDecision.OwnerValue,
            primaryDecision.TenantColumn, primaryDecision.TenantValue);

        foreach (var typeName in joinedTypeNames.Distinct().Where(t => !string.Equals(t, primary.TypeName, StringComparison.OrdinalIgnoreCase)))
        {
            var joinedSchema = registry.Get(typeName)
                ?? throw new RpcException(
                    new Status(
                        StatusCode.FailedPrecondition,
                        $"No schema registered for '{typeName}'."));
            var decision = authEvaluator.Evaluate(joinedSchema, actingUserAccessor.ActingUser, AuthorizationAction.Read);
            if (decision.Denied)
                return new AuthzResult(false, typeName, constraints);
            constraints[typeName] = new AuthorizationConstraint(
                decision.AllowedFields, decision.OwnerFieldName, decision.OwnerValue,
                decision.TenantColumn, decision.TenantValue);
        }

        return new AuthzResult(false, null, constraints);
    }

    private static void ValidateFilterProperty(SchemaDescriptor schema, string property, string rpcName)
    {
        var known = schema.ScalarColumns.Any(c => string.Equals(c.Name, property, StringComparison.OrdinalIgnoreCase))
                 || schema.FkColumns.Any(fk => string.Equals(fk.ColumnName, property, StringComparison.OrdinalIgnoreCase));
        if (!known)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"{rpcName}: filter property '{property}' is not a scalar or foreign-key column on '{schema.TypeName}'."));
    }

    /// <summary>
    /// The type's timestamp-typed scalar columns, camelCased to match both the payload keys
    /// IntelligenceStoreConsumer writes and the property spelling the filter builder sees.
    /// IntelligenceStoreConsumer stores these values in canonical round-trip ("o") form, so the
    /// filter builder must re-emit operands on them the same way or equality never matches.
    /// </summary>
    private static IReadOnlySet<string> TimestampColumns(SchemaDescriptor schema) =>
        schema.ScalarColumns
            .Where(c => c.SqlType.ToUpperInvariant() is "TIMESTAMPTZ" or "DATETIME")
            .Select(c => c.Name.ToCamelCase())
            .ToHashSet(StringComparer.Ordinal);

    private static Filter? BuildChunksFilter(
        SchemaDescriptor schema, SearchChunksRequest request, IReadOnlySet<string>? allowedFields)
    {
        if (request.Filter.Count == 0) return null;

        var filter = new Filter();
        var timestampColumns = TimestampColumns(schema);

        foreach (var clause in request.Filter)
        {
            if (clause.Operator != SearchOperator.Equals || clause.ClauseType != SearchClauseType.Filter)
                throw new RpcException(
                    new Status(
                        StatusCode.InvalidArgument,
                        "SearchChunks only supports EQUALS filter clauses; other operators and " +
                        "MUST_NOT clauses are rejected."));

            if (string.Equals(clause.Property, schema.KeyColumn.Name, StringComparison.OrdinalIgnoreCase))
                filter.Must.AddRange(IntelligenceFilterBuilder.MatchParentId(clause.Value.StringVal).Must);
            else if (schema.MetadataColumns.TryGetValue(clause.Property, out var canonicalName))
            {
                // The key clause is exempt from field masking (see exemptField: "Key" on the
                // SearchSimilar path, and parent_key is returned unconditionally here), but a
                // metadata column can be field-restricted — filtering on one the caller cannot
                // read would be a value oracle.
                // canonicalName is the schema's stored spelling; the caller's casing may differ
                // (MetadataColumns is OrdinalIgnoreCase) and both the payload key written by
                // IntelligenceStoreConsumer and AllowedFields use the schema's spelling.
                if (allowedFields is not null && !allowedFields.Contains(canonicalName))
                    throw new RpcException(new Status(StatusCode.InvalidArgument,
                        $"SearchChunks: filter property '{clause.Property}' is not authorized for this caller."));

                filter.Must.Add(IntelligenceFilterBuilder.MatchEquality(
                    canonicalName.ToCamelCase(), clause.Value, timestampColumns));
            }
            else
                throw new RpcException(
                    new Status(
                        StatusCode.InvalidArgument,
                        $"SearchChunks filter clauses must target the primary-key property " +
                        $"'{schema.KeyColumn.Name}' or a metadata column on '{schema.TypeName}', " +
                        $"got '{clause.Property}'."));
        }

        return filter;
    }

    private static EngagementAggSpec ProtoToEngagementSpec(ProtoAggSpec proto) =>
        new(
            Name:             proto.Name,
            Kind:             ProtoKindToSr(proto.Type),
            Field:            proto.Field,
            Size:             proto.Size > 0 ? proto.Size : 10,
            CalendarInterval: string.IsNullOrEmpty(proto.CalendarInterval) ? null : proto.CalendarInterval,
            TimeZone:         string.IsNullOrEmpty(proto.TimeZone)         ? null : proto.TimeZone,
            RangeBuckets:     proto.RangeBuckets.Count > 0
                ? proto.RangeBuckets.Select(b => new EngagementRangeSpec(b.Key, b.From, b.To)).ToList()
                : null,
            GroupByFields:    proto.GroupByFields.Count > 0 ? proto.GroupByFields.ToList() : null,
            Expression:       string.IsNullOrEmpty(proto.Expression) ? null : proto.Expression);

    private static SrAggKind ProtoKindToSr(AggregationType type) => type switch
    {
        AggregationType.Terms         => SrAggKind.Terms,
        AggregationType.DateHistogram => SrAggKind.DateHistogram,
        AggregationType.Range         => SrAggKind.Range,
        AggregationType.Avg           => SrAggKind.Avg,
        AggregationType.Sum           => SrAggKind.Sum,
        AggregationType.Min           => SrAggKind.Min,
        AggregationType.Max           => SrAggKind.Max,
        AggregationType.Count         => SrAggKind.Count,
        _                             => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    private static ProtoAggResult SrResultToProto(EngagementAggResult result)
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
        SrAggKind.Count         => AggregationType.Count,
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
        null             => Value.ForNull(),
        string s         => Value.ForString(s),
        bool b           => Value.ForBool(b),
        double d         => Value.ForNumber(d),
        float f          => Value.ForNumber(f),
        int i            => Value.ForNumber(i),
        long l           => Value.ForNumber(l),
        DateTime dt      => Value.ForString(dt.ToString("o")),
        DateTimeOffset o => Value.ForString(o.ToString("o")),
        _                => Value.ForString(v.ToString()!)
    };
}
