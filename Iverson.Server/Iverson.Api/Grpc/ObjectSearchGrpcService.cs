using System.Globalization;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Authorization;
using Iverson.Api.Consumers;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.StarRocks;
using Iverson.Vector;
using Microsoft.Extensions.Options;
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
    IResultReranker reranker,
    IResultDiversifier diversifier,
    IOptions<DecayOptions> decayOptions)
    : ObjectSearchService.ObjectSearchServiceBase
{
    private readonly DecayOptions _decayOptions = decayOptions.Value;

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

            // Result-side strip for the server-owned tenant column. This path never reaches
            // MaskDisallowedFields, so without it the SQL-side exclusion in the query builders is
            // the ONLY defence — and a joined-type `Type`.* wildcard over a physical table proved
            // that single point is bypassable. Unconditional, and independent of the AllowedFields
            // filtering below, which does nothing at all when AllowedFields is null.
            AuthorizationFieldMasking.RemoveTenantColumn(dict);

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

        // The centroid signal only exists for a property that is BOTH embedded and chunked —
        // "<property>_centroid" is written on the object collection only for chunk fields. For an
        // embedding-only property there is no such named vector.
        var centroidPossible = schema.ChunkFields.Any(c =>
            string.Equals(c.PropertyName, vectorDesc.PropertyName, StringComparison.OrdinalIgnoreCase));

        var decayField = DecayFieldResolver.ResolveDecayField(schema, logger);

        // When NEITHER signal can be present, the fused score provably equals the base score for
        // every candidate and the re-rank is a mathematical identity — Qdrant's own ordering is
        // already final. Over-fetching 4x then discarding 3/4 of the payloads (which carry the
        // full source text of every vector field) buys nothing, so fetch exactly topK. Whenever
        // either signal CAN be present the over-fetch stays exactly 4x with no ceiling.
        var rerankIsIdentity = !centroidPossible && decayField is null;
        var fetchLimit       = rerankIsIdentity ? topK : topK * OverFetchFactor;

        IReadOnlyList<VectorSearchResult> results;
        using (RequestHeaders.Use("api-key", tenantScope.MintScopedApiKey(collectionName, readOnly: true)))
        {
            try
            {
                results = await vector.SearchNamedAsync(collectionName, vectorName, queryVector, fetchLimit, filter);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                // Tenant's collection was never created (no writes yet) — treat as empty result
                // set rather than creating a collection just to search it. See design spec
                // §"Reads against a tenant with no collection yet".
                results = [];
            }
        }

        // No centroid vector to fetch when the property is not chunked — skip the round trip
        // entirely and let every candidate's centroid be absent.
        var centroids = EmptyVectors;
        if (results.Count > 0 && centroidPossible)
        {
            centroids = await RetrieveVectorsOrDegradeAsync(
                collectionName,
                results.Select(r => r.Id).ToList(),
                vectorDesc.PropertyName.ToSnakeCase() + "_centroid",
                "SearchSimilar",
                "re-ranking without the centroid signal");
        }

        var now = DateTimeOffset.UtcNow;

        var candidates = results.Select(r => new RerankCandidate(
            Id:        r.Id,
            BaseScore: r.Score,
            Centroid:  centroids.TryGetValue(r.Id, out var centroid) ? centroid : null,
            Decay:     DecayFor(r, decayField, now, _decayOptions.HalfLifeDays))).ToList();

        var byId = ResultsById(results);

        // A candidate whose diversity vector is ABSENT contributes no similarity term and so takes
        // no penalty, which means it outranks an otherwise-equal candidate that has a vector and
        // any positive similarity. Accepted by design: substituting a value would break the
        // bit-exact Take(topK) degradation guarantee. See the design spec's Known issues.
        var diversityCandidates = reranker.Rerank(queryVector, candidates)
            .Select(r => new DiversifyCandidate(
                r.Id,
                r.FusedScore,
                centroids.TryGetValue(r.Id, out var v) ? v : null))
            .ToList();

        // Camel-cased descriptor name → descriptor, built once per request rather than per row.
        // Covers ScalarColumns and KeyColumn, plus the explicit "key" special case:
        // IntelligenceStoreConsumer.cs:417 writes the identity value under the literal payload key
        // "key", which ToCamelCase("Id") would never produce, so it cannot be derived from the
        // lookup and must be seeded directly. StructSerializer.UpperFirst below only fires for a
        // payload key with no matching descriptor column at all — e.g. a stale key left behind by
        // a since-removed column.
        var columnLookup = new Dictionary<string, ColumnDescriptor>(StringComparer.Ordinal);
        foreach (var col in schema.ScalarColumns)
            columnLookup[col.Name.ToCamelCase()] = col;
        columnLookup[schema.KeyColumn.Name.ToCamelCase()] = schema.KeyColumn;
        columnLookup["key"] = schema.KeyColumn;

        foreach (var ranked in diversifier.Diversify(diversityCandidates, (int)topK))
        {
            if (!byId.TryGetValue(ranked.Id, out var r)) continue;

            var protoStruct = new Struct();
            foreach (var kvp in r.Payload)
            {
                if (columnLookup.TryGetValue(kvp.Key, out var col))
                    protoStruct.Fields[col.Name] = ConvertPayloadValue(kvp.Value, col.SqlType);
                else
                    protoStruct.Fields[StructSerializer.UpperFirst(kvp.Key)] = Value.ForString(kvp.Value);
            }

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

        // Unlike SearchSimilar, the identity gate can never fire here: SearchChunks only accepts a
        // property carrying [IversonChunk], and SchemaBuilder writes a "<property>_centroid" named
        // vector on the object collection for every chunk field. The centroid signal is therefore
        // always possible, so the over-fetch stays exactly 4x with no ceiling.
        var fetchLimit = topK * OverFetchFactor;

        IReadOnlyList<VectorSearchResult> results;
        using (RequestHeaders.Use("api-key", tenantScope.MintScopedApiKey(chunksCollection, readOnly: true)))
        {
            try
            {
                results = await vector.SearchNamedAsync(chunksCollection, vectorName, queryVector, fetchLimit, filter);
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

        var centroids = await RetrieveVectorsOrDegradeAsync(
            tenantScope.ResolveCollectionName(schema.CollectionName, decision.TenantValue, isChunks: false),
            parentIds,
            chunkDesc.PropertyName.ToSnakeCase() + "_centroid",
            "SearchChunks",
            "re-ranking without the centroid signal");

        // The DIVERSITY vector for a chunk is the chunk's OWN vector — the same representation
        // Qdrant matched the query against — not its parent centroid, which is the re-rank signal
        // above and lives at document granularity. Distinct collections, distinct signals.
        //
        // Skipped when diversification provably cannot act: MMR reads a diversity vector only
        // inside the selection loop, which runs only when Math.Min(topK, pool) >= 2. Below that the
        // retrieve cannot change the returned set OR its order. Deliberately NOT gated on
        // pool > topK — MMR reorders even when the pool is exactly topK.
        var chunkVectors = EmptyVectors;
        if (results.Count > 1 && topK > 1)
        {
            chunkVectors = await RetrieveVectorsOrDegradeAsync(
                chunksCollection,
                results.Select(r => r.Id).ToList(),
                vectorName,
                "SearchChunks",
                "selecting without the diversity signal");
        }

        var decayField = DecayFieldResolver.ResolveDecayField(schema, logger);
        var now        = DateTimeOffset.UtcNow;

        var candidates = results.Select(r =>
        {
            float[]? centroid = null;
            if (r.Payload.TryGetValue("parent_id", out var parent) && !string.IsNullOrEmpty(parent))
                centroids.TryGetValue(IntelligenceStoreConsumer.KeyToUlong(parent), out centroid);

            return new RerankCandidate(
                r.Id, r.Score, centroid, DecayFor(r, decayField, now, _decayOptions.HalfLifeDays));
        }).ToList();

        var byId = ResultsById(results);

        // A candidate whose diversity vector is ABSENT contributes no similarity term and so takes
        // no penalty, which means it outranks an otherwise-equal candidate that has a vector and
        // any positive similarity. Accepted by design: substituting a value would break the
        // bit-exact Take(topK) degradation guarantee. See the design spec's Known issues.
        var diversityCandidates = reranker.Rerank(queryVector, candidates)
            .Select(r => new DiversifyCandidate(
                r.Id,
                r.FusedScore,
                chunkVectors.TryGetValue(r.Id, out var v) ? v : null))
            .ToList();

        foreach (var ranked in diversifier.Diversify(diversityCandidates, (int)topK))
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

            // Result-side strip for the server-owned tenant column. This path never reaches
            // MaskDisallowedFields, so without it the SQL-side exclusion in the query builders is
            // the ONLY defence — and a joined-type `Type`.* wildcard over a physical table proved
            // that single point is bypassable. Unconditional, and independent of the AllowedFields
            // filtering below, which does nothing at all when AllowedFields is null.
            AuthorizationFieldMasking.RemoveTenantColumn(dict);
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

            // Result-side strip for the server-owned tenant column. This path never reaches
            // MaskDisallowedFields, so without it the SQL-side exclusion in the query builders is
            // the ONLY defence — and a joined-type `Type`.* wildcard over a physical table proved
            // that single point is bypassable. Unconditional, and independent of the AllowedFields
            // filtering below, which does nothing at all when AllowedFields is null.
            AuthorizationFieldMasking.RemoveTenantColumn(dict);
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

    private static readonly IReadOnlyDictionary<ulong, float[]> EmptyVectors =
        new Dictionary<ulong, float[]>();

    /// <summary>
    /// Retrieves a named vector for a set of point ids under its own scoped api-key. A failure here
    /// degrades the ranking rather than failing the search: every vector becomes ABSENT (never a
    /// substituted neutral value). The caller names the consequence so the log stays specific.
    /// </summary>
    private async Task<IReadOnlyDictionary<ulong, float[]>> RetrieveVectorsOrDegradeAsync(
        string collection, IReadOnlyList<ulong> ids, string vectorName, string rpcName, string consequence)
    {
        if (ids.Count == 0) return EmptyVectors;

        try
        {
            using (RequestHeaders.Use("api-key", tenantScope.MintScopedApiKey(collection, readOnly: true)))
                return await vector.RetrieveNamedVectorAsync(collection, ids, vectorName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "[{Rpc}] retrieve failed (collection={Collection} vector={Vector} ids={Count}); {Consequence}.",
                rpcName, collection.SanitizeForLog(), vectorName.SanitizeForLog(), ids.Count, consequence);
            return EmptyVectors;
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

    private static double? DecayFor(
        VectorSearchResult result, string? decayField, DateTimeOffset now, double halfLifeDays) =>
        decayField is not null && result.Payload.TryGetValue(decayField, out var stored)
            ? DecayFieldResolver.ComputeDecay(stored, now, halfLifeDays)
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
        // ScalarColumns position: EXCLUDE __TenantId. It is a real column, but it is not addressable
        // by clients: a filter naming it must be rejected with the same "unknown property" error any
        // typo gets, so a caller can neither probe nor override the tenant boundary through a filter.
        var known = schema.ScalarColumns.Any(c =>
                        !SchemaDescriptor.IsTenantColumn(c.Name) &&
                        string.Equals(c.Name, property, StringComparison.OrdinalIgnoreCase))
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
    internal static IReadOnlySet<string> TimestampColumns(SchemaDescriptor schema) =>
        schema.ScalarColumns
            // ScalarColumns position: EXCLUDE __TenantId. This set names the properties a caller may
            // write a timestamp operand against; the tenant column is not addressable by clients, so
            // it must never enter it. (SchemaBuilder types it TEXT, so today it cannot — this keeps
            // the exclusion true independently of that.)
            .Where(c => !SchemaDescriptor.IsTenantColumn(c.Name))
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

    // Exhaustive vocabulary per the design spec's §2, matched case-insensitively (StringComparer
    // .OrdinalIgnoreCase, matching SchemaBuilder.cs:391's SqlTypeMap) and EXACTLY — never by
    // prefix, so an array SqlType like "DOUBLE PRECISION[]" falls through to the string default
    // rather than prefix-matching "DOUBLE PRECISION". Array types are absent from the vocabulary
    // on purpose: the payload flattening upstream gives back no list to reconstruct, so they keep
    // the string form. Deliberately NOT SchemaBuilder.SqlTypeToPayloadKind — that method is
    // private to SchemaBuilder and assigns INTEGER[] the Integer kind even though an array's
    // payload value is a serialized string, which would mis-type arrays as numbers here.
    private static readonly HashSet<string> NumericSqlTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "INTEGER", "INT", "BIGINT", "REAL", "FLOAT", "DOUBLE", "DOUBLE PRECISION"
    };

    private static readonly HashSet<string> BooleanSqlTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "BOOLEAN"
    };

    // Converts a Qdrant payload's flattened string value to a typed proto Value, following the
    // resolved descriptor column's SqlType. A value that fails to parse falls back to the string
    // form rather than failing the row — the column's declared type is a hint about what the
    // producer wrote, not a guarantee every stored value still matches it.
    private static Value ConvertPayloadValue(string value, string sqlType)
    {
        if (NumericSqlTypes.Contains(sqlType))
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                ? Value.ForNumber(n)
                : Value.ForString(value);

        if (BooleanSqlTypes.Contains(sqlType))
            return bool.TryParse(value, out var b) ? Value.ForBool(b) : Value.ForString(value);

        // TEXT, STRING, UUID, TIMESTAMPTZ, DATETIME, BYTEA, VARBINARY, array types, and anything
        // unrecognized all keep the string form.
        return Value.ForString(value);
    }
}
