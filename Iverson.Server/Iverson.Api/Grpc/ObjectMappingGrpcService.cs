using System.Diagnostics;
using System.Text.RegularExpressions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Authorization;
using Iverson.Api.Reconciliation;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.Events;
using Iverson.Sql;
using Iverson.StarRocks;
using Iverson.Vector;
using Microsoft.AspNetCore.Authorization;
using SchemaRelationKind       = Iverson.Api.Schema.RelationKind;
using SchemaRelationDescriptor = Iverson.Api.Schema.RelationDescriptor;

namespace Iverson.Api.Grpc;

/// <summary>
/// Implements full entity CRUD with server-side relationship resolution.
/// Routing to backing stores (SQL / StarRocks / Qdrant / Kafka) is determined by
/// the server's entity schema — the client is ignorant of this mapping.
/// </summary>
public sealed class ObjectMappingGrpcService(
    IEntityRepository _entities,
    IRecordStoreTransactionRunner _txRunner,
    IRecordStoreSchemaManager _schemaManager,
    IVectorSchemaManager _vector,
    IEventProducer _events,
    SchemaRegistry _registry,
    IEmbeddingService _embedding,
    IEngagementStoreSchemaManager _starRocks,
    IRelationValidator _relationValidator,
    IEntityKeyAccessor _keyAccessor,
    IOutboxWriter _outboxWriter,
    ILogger<ObjectMappingGrpcService> _logger,
    IActingUserAccessor _actingUserAccessor,
    IRowFieldAuthorizationEvaluator _authEvaluator)
    : ObjectMappingService.ObjectMappingServiceBase
{
    private const string SchemaVersion = "1";

    // ── Schema registration ────────────────────────────────────────────────────

    [Authorize(Policy = "SchemaAdmin")]
    public override async Task<SchemaResponse> RegisterSchema(
        SchemaRequest request,
        ServerCallContext context)
    {
        _logger.LogInformation("[RegisterSchema] root={Type} dependents={Deps}",
            request.RootType?.TypeName?.SanitizeForLog(), request.Dependents.Count);

        if (request.RootType is null)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "root_type is required."));

        var registered = new List<string>();

        foreach (var typeDesc in new[] { request.RootType }.Concat(request.Dependents))
        {
            ValidateIdentifier(typeDesc.TypeName, "type_name");
            foreach (var property in typeDesc.Properties)
                ValidateIdentifier(property.Name, $"property name on type '{typeDesc.TypeName}'");

            var descriptor = SchemaBuilder.BuildDescriptor(typeDesc, _embedding);

            var ownerField = descriptor.Authorization?.OwnerField;
            if (!string.IsNullOrEmpty(ownerField) &&
                !descriptor.ScalarColumns.Any(c => string.Equals(c.Name, ownerField, StringComparison.OrdinalIgnoreCase)))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"owner_field '{ownerField}' on '{descriptor.TypeName}' does not match any declared scalar property."));
            }

            if (!string.IsNullOrEmpty(ownerField))
            {
                var ownerColumn = descriptor.ScalarColumns.First(c =>
                    string.Equals(c.Name, ownerField, StringComparison.OrdinalIgnoreCase));

                // Allow-list, not a reject-list: IntelligenceStoreConsumer.ExtractTypedValue's default branch
                // only produces a clean scalar string for these 4 SqlTypes. Every other SqlType — including
                // the array variants UUID[]/REAL[] that SchemaBuilder.ArrayTypeOverrides can also produce for
                // a scalar column — falls through to JsonElement.ToString(), which for a non-string JSON value
                // (a number, bool, or array) produces something that can never equal a real caller's identity
                // value, silently excluding every caller (including the legitimate owner) from every result.
                var stringValuedSqlTypes = new[] { "TEXT", "UUID", "BYTEA", "TIMESTAMPTZ" };
                if (!stringValuedSqlTypes.Contains(ownerColumn.SqlType.ToUpperInvariant()))
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument,
                        $"owner_field '{ownerField}' on '{descriptor.TypeName}' has SqlType '{ownerColumn.SqlType}', " +
                        "which is not string-valued; Qdrant ownership filtering requires a string-valued owner field."));
                }

                if (descriptor.ChunkFields.Count > 0)
                {
                    var reservedChunkKeys = new[] { "text", "parent_id", "field", "chunk_index" };
                    var camelOwnerField = ownerField.ToCamelCase();
                    if (reservedChunkKeys.Contains(camelOwnerField))
                    {
                        throw new RpcException(new Status(StatusCode.InvalidArgument,
                            $"owner_field '{ownerField}' on '{descriptor.TypeName}' camelCases to '{camelOwnerField}', " +
                            $"which collides with a reserved chunk-payload key ({string.Join(", ", reservedChunkKeys)})."));
                    }
                }
            }

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
            request.TypeName.SanitizeForLog(), request.Key, request.Depth);

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

        var decision = _authEvaluator.Evaluate(schema, _actingUserAccessor.ActingUser, AuthorizationAction.Read);
        if (decision.Denied ||
            (decision.OwnershipRequired &&
             StructFieldAccess.GetFieldString(entityStruct, decision.OwnerFieldName!) != decision.OwnerValue))
        {
            return new MappingResponse
            {
                Success = false,
                Error   = $"'{request.TypeName}:{request.Key}' not found.",
                TraceId = request.TraceId
            };
        }

        AuthorizationFieldMasking.MaskDisallowedFields(entityStruct, decision.AllowedFields);

        if (request.Depth > 0)
            await ResolveRelationsAsync(entityStruct, schema, request.Depth, context.CancellationToken);

        return new MappingResponse { Success = true, Data = entityStruct, TraceId = request.TraceId };
    }

    public override async Task<MappingResponse> Post(
        MappingWriteRequest request,
        ServerCallContext context)
    {
        _logger.LogInformation("[Mapping.Post] type={Type}", request.TypeName.SanitizeForLog());

        var schema = RequireSchema(request.TypeName);

        AuthorizationFieldMasking.EnforceWriteAuthorization(
            _authEvaluator, _actingUserAccessor.ActingUser, schema, request.Payload,
            AuthorizationAction.Write, "Not authorized to create this entity.", existingRowJson: null);

        _relationValidator.ValidateRelations(request.Payload, schema);

        var key = _keyAccessor.ExtractKey(request.Payload, schema.KeyColumn.Name);
        if (string.IsNullOrWhiteSpace(key) || key == Guid.Empty.ToString())
        {
            key = Guid.CreateVersion7().ToString();
            _keyAccessor.SetKey(request.Payload, schema.KeyColumn.Name, key);
        }

        var payloadJson = StructSerializer.SerializePayload(request.Payload);

        var outboxRowId = await _outboxWriter.UpsertAndEnqueueOutboxAsync(SchemaBuilder.ToTableSchema(schema), request.TypeName, key, payloadJson);
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
                EntityTopics.Events,
                key,
                new EntityEvent(
                    EntityEventType.Created,
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
                "ReconciliationQueueWorker will retry from the durable outbox row", request.TypeName.SanitizeForLog(), key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[Mapping.Post] Publish succeeded but outbox cleanup failed for type={Type} key={Key} — " +
                "ReconciliationQueueWorker will harmlessly re-publish from the durable outbox row", request.TypeName.SanitizeForLog(), key);
        }

        return new MappingResponse { Success = true, Data = request.Payload, TraceId = request.TraceId };
    }

    public override async Task<MappingResponse> Update(
        MappingWriteRequest request,
        ServerCallContext context)
    {
        _logger.LogInformation("[Mapping.Update] type={Type}", request.TypeName.SanitizeForLog());

        var schema = RequireSchema(request.TypeName);

        var key = _keyAccessor.ExtractKey(request.Payload, schema.KeyColumn.Name);
        if (string.IsNullOrWhiteSpace(key) || key == Guid.Empty.ToString())
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Update requires a non-empty '{schema.KeyColumn.Name}' in the payload."));

        var existingRowJson = await FetchByKeyAsync(schema, key);
        AuthorizationFieldMasking.EnforceWriteAuthorization(
            _authEvaluator, _actingUserAccessor.ActingUser, schema, request.Payload,
            AuthorizationAction.Write, "Not authorized to update this entity.", existingRowJson);

        _relationValidator.ValidateRelations(request.Payload, schema);

        var payloadJson = StructSerializer.SerializePayload(request.Payload);

        var outboxRowId = await _outboxWriter.UpsertAndEnqueueOutboxAsync(SchemaBuilder.ToTableSchema(schema), request.TypeName, key, payloadJson);
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
                EntityTopics.Events,
                key,
                new EntityEvent(
                    EntityEventType.Updated,
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
                "ReconciliationQueueWorker will retry from the durable outbox row", request.TypeName.SanitizeForLog(), key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[Mapping.Update] Publish succeeded but outbox cleanup failed for type={Type} key={Key} — " +
                "ReconciliationQueueWorker will harmlessly re-publish from the durable outbox row", request.TypeName.SanitizeForLog(), key);
        }

        return new MappingResponse { Success = true, Data = request.Payload, TraceId = request.TraceId };
    }

    public override async Task<MappingDeleteResponse> Delete(
        MappingDeleteRequest request, ServerCallContext context)
    {
        _logger.LogInformation("[Mapping.Delete] type={Type} key={Key}", request.TypeName.SanitizeForLog(), request.Key);

        var schema = RequireSchema(request.TypeName);

        var rowJson = await FetchByKeyAsync(schema, request.Key);
        if (rowJson is null)
            return new MappingDeleteResponse
            {
                Success = false,
                Error   = $"'{request.TypeName}:{request.Key}' not found.",
                TraceId = request.TraceId
            };

        var decision = _authEvaluator.Evaluate(schema, _actingUserAccessor.ActingUser, AuthorizationAction.Delete);
        if (decision.Denied ||
            (decision.OwnershipRequired &&
             StructFieldAccess.GetFieldString(JsonParser.Default.Parse<Struct>(rowJson), decision.OwnerFieldName!) != decision.OwnerValue))
        {
            return new MappingDeleteResponse
            {
                Success = false,
                Error   = $"'{request.TypeName}:{request.Key}' not found.",
                TraceId = request.TraceId
            };
        }

        var targetStores = StoreTargeting.DetermineTargetStores(schema);
        var outboxRowId  = Guid.CreateVersion7();

        await _txRunner.ExecuteInTransactionAsync(async tx =>
        {
            await _entities.DeleteAsync(tx, SchemaBuilder.ToTableSchema(schema), request.Key);

            await _outboxWriter.EnqueueDeleteOutboxRowAsync(
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
                EntityTopics.Events,
                request.Key,
                new EntityEvent(
                    EntityEventType.Deleted,
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
                "ReconciliationQueueWorker will retry from the durable outbox row", request.TypeName.SanitizeForLog(), request.Key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[Mapping.Delete] Publish succeeded but outbox cleanup failed for type={Type} key={Key} — " +
                "ReconciliationQueueWorker will harmlessly re-publish from the durable outbox row", request.TypeName.SanitizeForLog(), request.Key);
        }

        return new MappingDeleteResponse { Success = true, TraceId = request.TraceId };
    }

    // ── Identifier validation ────────────────────────────────────────────────

    // TypeName/property names are string-interpolated unescaped into CREATE TABLE/ALTER TABLE
    // DDL by PostgresSchemaManager/StarRocksSchemaManager after only a case transformation
    // (NamingExtensions.ToSnakeCase, which does not escape or reject anything). Validate at
    // the source — every descriptor that reaches SchemaBuilder.BuildDescriptor must already be
    // a safe DDL identifier. No underscores are permitted in the input because ToSnakeCase
    // inserts its own; this pattern also naturally rejects an empty string.
    private static readonly Regex IdentifierPattern = new("^[A-Za-z][A-Za-z0-9]*$", RegexOptions.Compiled);

    private static void ValidateIdentifier(string name, string context)
    {
        if (!IdentifierPattern.IsMatch(name))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"{context} '{name}' is not a valid identifier — it must start with a letter and contain only letters and digits."));
        }
    }

    // ── SQL helpers ───────────────────────────────────────────────────────────

    private SchemaDescriptor RequireSchema(string typeName) =>
        _registry.Get(typeName) ?? throw new RpcException(new Status(StatusCode.FailedPrecondition,
            $"No schema registered for '{typeName}'. Call RegisterSchema first."));

    private Task<string?> FetchByKeyAsync(SchemaDescriptor schema, string key) =>
        _entities.FetchByKeyAsync(SchemaBuilder.ToTableSchema(schema), key);

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

                default:
                    throw new ArgumentOutOfRangeException(nameof(relation.Kind), relation.Kind,
                        $"Unhandled {nameof(SchemaRelationKind)} value in relation resolution — add a case above.");
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

        var decision = _authEvaluator.Evaluate(relatedSchema, _actingUserAccessor.ActingUser, AuthorizationAction.Read);
        if (decision.Denied ||
            (decision.OwnershipRequired &&
             StructFieldAccess.GetFieldString(relatedStruct, decision.OwnerFieldName!) != decision.OwnerValue))
            return;
        AuthorizationFieldMasking.MaskDisallowedFields(relatedStruct, decision.AllowedFields);

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

        var rows = await _entities.FetchManyByKeysAsync(SchemaBuilder.ToTableSchema(relatedSchema), ids);

        var rowsByKey = rows.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);

        var decision = _authEvaluator.Evaluate(relatedSchema, _actingUserAccessor.ActingUser, AuthorizationAction.Read);

        var items = new List<Value>();
        foreach (var id in ids)
        {
            if (ct.IsCancellationRequested) break;
            if (!rowsByKey.TryGetValue(id, out var row)) continue;
            var relatedStruct = JsonParser.Default.Parse<Struct>(row.Data);

            if (decision.Denied ||
                (decision.OwnershipRequired &&
                 StructFieldAccess.GetFieldString(relatedStruct, decision.OwnerFieldName!) != decision.OwnerValue))
                continue;
            AuthorizationFieldMasking.MaskDisallowedFields(relatedStruct, decision.AllowedFields);

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

        var rows = await _entities.FetchByColumnAsync(
            SchemaBuilder.ToTableSchema(relatedSchema), relation.ForeignKey, keyValue);

        var decision = _authEvaluator.Evaluate(relatedSchema, _actingUserAccessor.ActingUser, AuthorizationAction.Read);

        var items = new List<Value>();
        foreach (var rowJson in rows)
        {
            if (ct.IsCancellationRequested) break;
            var relatedStruct = JsonParser.Default.Parse<Struct>(rowJson);

            if (decision.Denied ||
                (decision.OwnershipRequired &&
                 StructFieldAccess.GetFieldString(relatedStruct, decision.OwnerFieldName!) != decision.OwnerValue))
                continue;
            AuthorizationFieldMasking.MaskDisallowedFields(relatedStruct, decision.AllowedFields);

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
