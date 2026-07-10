using System.Diagnostics;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Reconciliation;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.Events;
using Iverson.Sql;
using Iverson.StarRocks;
using Iverson.Vector;
using SchemaRelationKind       = Iverson.Api.Schema.RelationKind;
using SchemaRelationDescriptor = Iverson.Api.Schema.RelationDescriptor;

namespace Iverson.Api.Grpc;

/// <summary>
/// Implements full entity CRUD with server-side relationship resolution.
/// Routing to backing stores (SQL / StarRocks / Qdrant / Kafka) is determined by
/// the server's entity schema — the client is ignorant of this mapping.
/// </summary>
public sealed class ObjectMappingGrpcService(
    IPostgresQueryExecutor _sql,
    IPostgresTransactionRunner _txRunner,
    IPostgresSchemaManager _schemaManager,
    IVectorSchemaManager _vector,
    IEventProducer _events,
    SchemaRegistry _registry,
    IEmbeddingService _embedding,
    IStarRocksSchemaManager _starRocks,
    IRelationValidator _relationValidator,
    IEntityKeyAccessor _keyAccessor,
    IOutboxWriter _outboxWriter,
    ILogger<ObjectMappingGrpcService> _logger)
    : ObjectMappingService.ObjectMappingServiceBase
{
    private const string SchemaVersion = "1";

    // ── Schema registration ────────────────────────────────────────────────────

    public override async Task<SchemaResponse> RegisterSchema(
        SchemaRequest request,
        ServerCallContext context)
    {
        _logger.LogInformation("[RegisterSchema] root={Type} dependents={Deps}",
            request.RootType?.TypeName, request.Dependents.Count);

        if (request.RootType is null)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "root_type is required."));

        var registered = new List<string>();

        foreach (var typeDesc in new[] { request.RootType }.Concat(request.Dependents))
        {
            var descriptor = SchemaBuilder.BuildDescriptor(typeDesc, _embedding);

            await _schemaManager.ApplySchemaAsync(SchemaBuilder.ToTableSchema(descriptor));

            try
            {
                await _starRocks.ApplyTableAsync(SchemaBuilder.ToStarRocksTableSchema(descriptor));
            }
            catch (StarRocksNotReadyException ex)
            {
                throw new RpcException(new Status(StatusCode.Unavailable,
                    $"StarRocks is not ready: {ex.Message}"));
            }

            if (descriptor.VectorFields.Count > 0)
                await _vector.ApplyCollectionAsync(SchemaBuilder.ToCollectionSchema(descriptor));

            if (descriptor.ChunkFields.Count > 0)
                await _vector.ApplyCollectionAsync(SchemaBuilder.ToChunkCollectionSchema(descriptor));

            await _registry.RegisterAsync(descriptor);
            registered.Add(descriptor.TypeName);
        }

        return new SchemaResponse
        {
            Success    = true,
            TraceId    = request.TraceId,
            Registered = { registered }
        };
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    public override async Task<MappingResponse> Get(
        MappingGetRequest request,
        ServerCallContext context)
    {
        _logger.LogInformation("[Mapping.Get] type={Type} key={Key} depth={Depth}",
            request.TypeName, request.Key, request.Depth);

        var schema = RequireSchema(request.TypeName);

        var rowJson = await FetchByKeyAsync(schema, request.Key);
        if (rowJson is null)
            return new MappingResponse
            {
                Success = false,
                Error   = $"'{request.TypeName}:{request.Key}' not found.",
                TraceId = request.TraceId
            };

        var entityStruct = JsonParser.Default.Parse<Struct>(rowJson);

        if (request.Depth > 0)
            await ResolveRelationsAsync(entityStruct, schema, request.Depth, context.CancellationToken);

        return new MappingResponse { Success = true, Data = entityStruct, TraceId = request.TraceId };
    }

    public override async Task<MappingResponse> Post(
        MappingWriteRequest request,
        ServerCallContext context)
    {
        _logger.LogInformation("[Mapping.Post] type={Type}", request.TypeName);

        var schema = RequireSchema(request.TypeName);

        _relationValidator.ValidateRelations(request.Payload, schema);

        var key = _keyAccessor.ExtractKey(request.Payload, schema.KeyColumn.Name);
        if (string.IsNullOrWhiteSpace(key) || key == Guid.Empty.ToString())
        {
            key = Guid.CreateVersion7().ToString();
            _keyAccessor.SetKey(request.Payload, schema.KeyColumn.Name, key);
        }

        var payloadJson = StructSerializer.SerializePayload(request.Payload);

        var outboxRowId = await _outboxWriter.UpsertAndEnqueueOutboxAsync(schema, request.TypeName, key, payloadJson);
        var targetStores = StoreTargeting.DetermineTargetStores(schema);

        // Opportunistic fast-path publish: the durability guarantee already exists (the
        // outbox row committed above, in the same transaction as the entity write), so a
        // failure here is not data loss — the existing ReconciliationQueueWorker (which now
        // polls unconditionally-inserted outbox rows, not just failure-recorded ones — see
        // Task 5's updated ReconciliationSchema doc comment) will pick this row up on its
        // next poll. This just keeps the common case's projection latency low.
        var published = false;
        try
        {
            await _events.ProduceAsync(
                EntityTopics.Created,
                key,
                new EntityEvent(
                    request.TypeName,
                    key,
                    payloadJson,
                    request.TraceId.NullIfEmpty() ?? Activity.Current?.TraceId.ToString() ?? string.Empty,
                    SchemaVersion,
                    DateTimeOffset.UtcNow,
                    targetStores));
            published = true;
            await _outboxWriter.DeleteOutboxRowIfPresentAsync(outboxRowId);
        }
        catch (Exception ex) when (!published)
        {
            _logger.LogWarning(ex,
                "[Mapping.Post] Opportunistic publish failed for type={Type} key={Key} — " +
                "ReconciliationQueueWorker will retry from the durable outbox row", request.TypeName, key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[Mapping.Post] Publish succeeded but outbox cleanup failed for type={Type} key={Key} — " +
                "ReconciliationQueueWorker will harmlessly re-publish from the durable outbox row", request.TypeName, key);
        }

        return new MappingResponse { Success = true, Data = request.Payload, TraceId = request.TraceId };
    }

    public override async Task<MappingResponse> Update(
        MappingWriteRequest request,
        ServerCallContext context)
    {
        _logger.LogInformation("[Mapping.Update] type={Type}", request.TypeName);

        var schema = RequireSchema(request.TypeName);

        var key = _keyAccessor.ExtractKey(request.Payload, schema.KeyColumn.Name);
        if (string.IsNullOrWhiteSpace(key) || key == Guid.Empty.ToString())
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Update requires a non-empty '{schema.KeyColumn.Name}' in the payload."));

        _relationValidator.ValidateRelations(request.Payload, schema);

        var payloadJson = StructSerializer.SerializePayload(request.Payload);

        var outboxRowId = await _outboxWriter.UpsertAndEnqueueOutboxAsync(schema, request.TypeName, key, payloadJson);
        var targetStores = StoreTargeting.DetermineTargetStores(schema);

        // Opportunistic fast-path publish: the durability guarantee already exists (the
        // outbox row committed above, in the same transaction as the entity write), so a
        // failure here is not data loss — the existing ReconciliationQueueWorker (which now
        // polls unconditionally-inserted outbox rows, not just failure-recorded ones — see
        // Task 5's updated ReconciliationSchema doc comment) will pick this row up on its
        // next poll. This just keeps the common case's projection latency low.
        var published = false;
        try
        {
            await _events.ProduceAsync(
                EntityTopics.Updated,
                key,
                new EntityEvent(
                    request.TypeName,
                    key,
                    payloadJson,
                    request.TraceId.NullIfEmpty() ?? Activity.Current?.TraceId.ToString() ?? string.Empty,
                    SchemaVersion,
                    DateTimeOffset.UtcNow,
                    targetStores));
            published = true;
            await _outboxWriter.DeleteOutboxRowIfPresentAsync(outboxRowId);
        }
        catch (Exception ex) when (!published)
        {
            _logger.LogWarning(ex,
                "[Mapping.Update] Opportunistic publish failed for type={Type} key={Key} — " +
                "ReconciliationQueueWorker will retry from the durable outbox row", request.TypeName, key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[Mapping.Update] Publish succeeded but outbox cleanup failed for type={Type} key={Key} — " +
                "ReconciliationQueueWorker will harmlessly re-publish from the durable outbox row", request.TypeName, key);
        }

        return new MappingResponse { Success = true, Data = request.Payload, TraceId = request.TraceId };
    }

    public override async Task<MappingDeleteResponse> Delete(
        MappingDeleteRequest request, ServerCallContext context)
    {
        _logger.LogInformation("[Mapping.Delete] type={Type} key={Key}", request.TypeName, request.Key);

        var schema = RequireSchema(request.TypeName);

        var rowJson = await FetchByKeyAsync(schema, request.Key);
        if (rowJson is null)
            return new MappingDeleteResponse
            {
                Success = false,
                Error   = $"'{request.TypeName}:{request.Key}' not found.",
                TraceId = request.TraceId
            };

        var targetStores = StoreTargeting.DetermineTargetStores(schema);
        var outboxRowId  = Guid.CreateVersion7();

        await _txRunner.ExecuteInTransactionAsync(async tx =>
        {
            await tx.ExecuteAsync(
                $"DELETE FROM \"{schema.TableName}\" WHERE \"{schema.KeyColumn.Name}\" = @Key::uuid",
                new { Key = request.Key });

            await ReconciliationSchema.EnqueueDeleteOutboxRowAsync(
                tx, outboxRowId, request.TypeName, request.Key, rowJson);
        });

        // Opportunistic fast-path publish: the durability guarantee already exists (the
        // delete-outbox row committed above, in the same transaction as the entity delete),
        // so a failure here is not data loss — the existing ReconciliationQueueWorker (which
        // now polls unconditionally-inserted outbox rows, not just failure-recorded ones —
        // see Task 5's updated ReconciliationSchema doc comment) will pick this row up on its
        // next poll and replay it from the stored pre-delete snapshot. This just keeps the
        // common case's projection latency low.
        var published = false;
        try
        {
            await _events.ProduceAsync(
                EntityTopics.Deleted,
                request.Key,
                new EntityEvent(
                    request.TypeName,
                    request.Key,
                    rowJson,
                    request.TraceId.NullIfEmpty() ?? Activity.Current?.TraceId.ToString() ?? string.Empty,
                    SchemaVersion,
                    DateTimeOffset.UtcNow,
                    targetStores));
            published = true;
            await _outboxWriter.DeleteOutboxRowIfPresentAsync(outboxRowId);
        }
        catch (Exception ex) when (!published)
        {
            _logger.LogWarning(ex,
                "[Mapping.Delete] Opportunistic publish failed for type={Type} key={Key} — " +
                "ReconciliationQueueWorker will retry from the durable outbox row", request.TypeName, request.Key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[Mapping.Delete] Publish succeeded but outbox cleanup failed for type={Type} key={Key} — " +
                "ReconciliationQueueWorker will harmlessly re-publish from the durable outbox row", request.TypeName, request.Key);
        }

        return new MappingDeleteResponse { Success = true, TraceId = request.TraceId };
    }

    // ── SQL helpers ───────────────────────────────────────────────────────────

    private SchemaDescriptor RequireSchema(string typeName) =>
        _registry.Get(typeName) ?? throw new RpcException(new Status(StatusCode.FailedPrecondition,
            $"No schema registered for '{typeName}'. Call RegisterSchema first."));

    private async Task<string?> FetchByKeyAsync(SchemaDescriptor schema, string key) =>
        await _sql.QuerySingleOrDefaultAsync<string>(
            $"SELECT row_to_json(t)::text FROM \"{schema.TableName}\" t WHERE \"{schema.KeyColumn.Name}\" = @Key::uuid",
            new { Key = key });

    // ── Relation resolution ───────────────────────────────────────────────────

    private async Task ResolveRelationsAsync(
        Struct entityStruct, SchemaDescriptor schema, int depth, CancellationToken ct)
    {
        foreach (var relation in schema.Relations)
        {
            switch (relation.Kind)
            {
                case SchemaRelationKind.ManyToOne:
                case SchemaRelationKind.OneToOne:
                    await ResolveSingleRelationAsync(entityStruct, relation, depth, ct);
                    break;

                case SchemaRelationKind.ManyToMany:
                    await ResolveManyToManyAsync(entityStruct, relation, depth, ct);
                    break;

                case SchemaRelationKind.OneToMany:
                    await ResolveOneToManyAsync(entityStruct, schema, relation, depth, ct);
                    break;
            }
        }
    }

    private async Task ResolveSingleRelationAsync(
        Struct entityStruct, SchemaRelationDescriptor relation, int depth, CancellationToken ct)
    {
        var fkValue = StructFieldAccess.GetFieldString(entityStruct, relation.ForeignKey);
        if (string.IsNullOrWhiteSpace(fkValue)) return;

        var relatedSchema = _registry.Get(relation.RelatedTypeName);
        if (relatedSchema is null) return;

        var rowJson = await FetchByKeyAsync(relatedSchema, fkValue);
        if (rowJson is null) return;

        var relatedStruct = JsonParser.Default.Parse<Struct>(rowJson);
        if (depth > 1)
            await ResolveRelationsAsync(relatedStruct, relatedSchema, depth - 1, ct);

        entityStruct.Fields[relation.PropertyName] = Value.ForStruct(relatedStruct);
    }

    private async Task ResolveManyToManyAsync(
        Struct entityStruct, SchemaRelationDescriptor relation, int depth, CancellationToken ct)
    {
        var ids = StructFieldAccess.GetFieldStringList(entityStruct, relation.ForeignKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0) return;

        var relatedSchema = _registry.Get(relation.RelatedTypeName);
        if (relatedSchema is null) return;

        var idGuids = ids.Select(Guid.Parse).ToArray();
        var rows = await _sql.QueryAsync<KeyedRow>(
            $"SELECT \"{relatedSchema.KeyColumn.Name}\"::text AS key, " +
            $"row_to_json(t)::text AS data " +
            $"FROM \"{relatedSchema.TableName}\" t " +
            $"WHERE \"{relatedSchema.KeyColumn.Name}\" = ANY(@ids)",
            new { ids = idGuids });

        var rowsByKey = rows.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);

        var items = new List<Value>();
        foreach (var id in ids)
        {
            if (ct.IsCancellationRequested) break;
            if (!rowsByKey.TryGetValue(id, out var row)) continue;
            var relatedStruct = JsonParser.Default.Parse<Struct>(row.Data);
            if (depth > 1)
                await ResolveRelationsAsync(relatedStruct, relatedSchema, depth - 1, ct);
            items.Add(Value.ForStruct(relatedStruct));
        }

        entityStruct.Fields[relation.PropertyName] = Value.ForList(items.ToArray());
    }

    private async Task ResolveOneToManyAsync(
        Struct entityStruct, SchemaDescriptor schema, SchemaRelationDescriptor relation, int depth, CancellationToken ct)
    {
        var keyValue = StructFieldAccess.GetFieldString(entityStruct, schema.KeyColumn.Name);
        if (string.IsNullOrWhiteSpace(keyValue)) return;

        var relatedSchema = _registry.Get(relation.RelatedTypeName);
        if (relatedSchema is null) return;

        var rows = await _sql.QueryAsync<string>(
            $"SELECT row_to_json(t)::text FROM \"{relatedSchema.TableName}\" t WHERE \"{relation.ForeignKey}\" = @Key::uuid",
            new { Key = keyValue });

        var items = new List<Value>();
        foreach (var rowJson in rows)
        {
            if (ct.IsCancellationRequested) break;
            var relatedStruct = JsonParser.Default.Parse<Struct>(rowJson);
            if (depth > 1)
                await ResolveRelationsAsync(relatedStruct, relatedSchema, depth - 1, ct);
            items.Add(Value.ForStruct(relatedStruct));
        }

        entityStruct.Fields[relation.PropertyName] = Value.ForList(items.ToArray());
    }
}

file static class StringExtensions
{
    internal static string? NullIfEmpty(this string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;
}
