using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Authorization;
using Iverson.Api.Reconciliation;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Events;
using Iverson.Sql;
using Microsoft.AspNetCore.Authorization;

namespace Iverson.Api.Grpc;

/// <summary>
/// Implements full entity CRUD with server-side relationship resolution.
/// Routing to backing stores (SQL / StarRocks / Qdrant / Kafka) is determined by
/// the server's entity schema — the client is ignorant of this mapping.
/// </summary>
public sealed class ObjectMappingGrpcService(
    IEntityRepository _entities,
    IRecordStoreTransactionRunner _txRunner,
    IOutboxPublisher _outboxPublisher,
    SchemaRegistry _registry,
    IRelationValidator _relationValidator,
    IEntityKeyAccessor _keyAccessor,
    IOutboxWriter _outboxWriter,
    ILogger<ObjectMappingGrpcService> _logger,
    IActingUserAccessor _actingUserAccessor,
    IRowFieldAuthorizationEvaluator _authEvaluator,
    IEntityRelationResolver _relationResolver,
    ISchemaRegistrationOrchestrator _schemaRegistration)
    : ObjectMappingService.ObjectMappingServiceBase
{
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

        var registered = await _schemaRegistration.RegisterAsync(request, context.CancellationToken);

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

        var rowJson = await FetchByKeyAsync(schema, request.Key,
            tenantScoped: schema.TenantColumn is not null,
            tenantId: _actingUserAccessor.ActingUser?.FindFirst("tenant_id")?.Value);
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
             StructFieldAccess.GetFieldString(entityStruct, decision.OwnerFieldName!) != decision.OwnerValue) ||
            (decision.TenantColumn is not null &&
             StructFieldAccess.GetFieldString(entityStruct, decision.TenantColumn) != decision.TenantValue))
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
            await _relationResolver.ResolveRelationsAsync(entityStruct, schema, request.Depth, _actingUserAccessor.ActingUser, context.CancellationToken);

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

        var decision = _authEvaluator.Evaluate(schema, _actingUserAccessor.ActingUser, AuthorizationAction.Write);
        var outboxRowId = await _outboxWriter.UpsertAndEnqueueOutboxAsync(
            SchemaBuilder.ToTableSchema(schema), request.TypeName, key, payloadJson,
            tenantId: decision.TenantValue);
        var targetStores = StoreTargeting.DetermineTargetStores(schema);

        // Opportunistic fast-path publish: the durability guarantee already exists (the
        // outbox row committed above, in the same transaction as the entity write), so a
        // failure here is not data loss — the existing ReconciliationQueueWorker (which now
        // polls unconditionally-inserted outbox rows, not just failure-recorded ones — see
        // Task 5's updated ReconciliationSchema doc comment) will pick this row up on its
        // next poll. This just keeps the common case's projection latency low.
        await _outboxPublisher.PublishAsync(EntityEventType.Created, request.TypeName, key, payloadJson,
            request.TraceId, targetStores, outboxRowId, "Mapping.Post");

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

        var decision = _authEvaluator.Evaluate(schema, _actingUserAccessor.ActingUser, AuthorizationAction.Write);
        var outboxRowId = await _outboxWriter.UpsertAndEnqueueOutboxAsync(
            SchemaBuilder.ToTableSchema(schema), request.TypeName, key, payloadJson,
            tenantId: decision.TenantValue);
        var targetStores = StoreTargeting.DetermineTargetStores(schema);

        // Opportunistic fast-path publish: the durability guarantee already exists (the
        // outbox row committed above, in the same transaction as the entity write), so a
        // failure here is not data loss — the existing ReconciliationQueueWorker (which now
        // polls unconditionally-inserted outbox rows, not just failure-recorded ones — see
        // Task 5's updated ReconciliationSchema doc comment) will pick this row up on its
        // next poll. This just keeps the common case's projection latency low.
        await _outboxPublisher.PublishAsync(EntityEventType.Updated, request.TypeName, key, payloadJson,
            request.TraceId, targetStores, outboxRowId, "Mapping.Update");

        return new MappingResponse { Success = true, Data = request.Payload, TraceId = request.TraceId };
    }

    public override async Task<MappingDeleteResponse> Delete(
        MappingDeleteRequest request, ServerCallContext context)
    {
        _logger.LogInformation("[Mapping.Delete] type={Type} key={Key}", request.TypeName.SanitizeForLog(), request.Key);

        var schema = RequireSchema(request.TypeName);

        var rowJson = await FetchByKeyAsync(schema, request.Key,
            tenantScoped: schema.TenantColumn is not null,
            tenantId: _actingUserAccessor.ActingUser?.FindFirst("tenant_id")?.Value);
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
             StructFieldAccess.GetFieldString(JsonParser.Default.Parse<Struct>(rowJson), decision.OwnerFieldName!) != decision.OwnerValue) ||
            (decision.TenantColumn is not null &&
             StructFieldAccess.GetFieldString(JsonParser.Default.Parse<Struct>(rowJson), decision.TenantColumn) != decision.TenantValue))
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
            await _entities.DeleteAsync(
                tx, SchemaBuilder.ToTableSchema(schema), request.Key,
                tenantScoped: decision.TenantColumn is not null,
                tenantId: decision.TenantValue);

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
        await _outboxPublisher.PublishAsync(EntityEventType.Deleted, request.TypeName, request.Key, rowJson,
            request.TraceId, targetStores, outboxRowId, "Mapping.Delete");

        return new MappingDeleteResponse { Success = true, TraceId = request.TraceId };
    }

    // ── SQL helpers ───────────────────────────────────────────────────────────

    private SchemaDescriptor RequireSchema(string typeName) =>
        _registry.Get(typeName) ?? throw new RpcException(new Status(StatusCode.FailedPrecondition,
            $"No schema registered for '{typeName}'. Call RegisterSchema first."));

    private Task<string?> FetchByKeyAsync(
        SchemaDescriptor schema, string key, bool tenantScoped = false, string? tenantId = null) =>
        _entities.FetchByKeyAsync(SchemaBuilder.ToTableSchema(schema), key, tenantScoped, tenantId);
}
