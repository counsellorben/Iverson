using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Iverson.Api.Authorization;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Events;
using Iverson.Sql;
using Iverson.StarRocks;
using Microsoft.AspNetCore.Authorization;
using Npgsql;
using ContractsRelationKind = Iverson.Client.Contracts.RelationKind;
using SchemaRelationKind    = Iverson.Api.Schema.RelationKind;

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
    IPayloadSizeValidator _payloadSizeValidator,
    IEntityKeyAccessor _keyAccessor,
    IOutboxWriter _outboxWriter,
    ILogger<ObjectMappingGrpcService> _logger,
    IActingUserAccessor _actingUserAccessor,
    IRowFieldAuthorizationEvaluator _authEvaluator,
    IEntityRelationResolver _relationResolver,
    ISchemaRegistrationOrchestrator _schemaRegistration,
    AuditLog _auditLog,
    EngagementQueryLimitOptions _queryLimits)
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

        var ownerTenantId = _actingUserAccessor.ActingUser?.FindFirst("tenant_id")?.Value;
        var registered = await _schemaRegistration.RegisterAsync(request, ownerTenantId, context.CancellationToken);

        _auditLog.AdminOperation(context.GetHttpContext().User, "RegisterSchema", request.RootType.TypeName);

        return new SchemaResponse
        {
            Success    = true,
            TraceId    = request.TraceId,
            Registered = { registered }
        };
    }

    // No [Authorize] here — GetSchema is discovery, meant to be reachable by any authenticated
    // caller. It inherits the ambient RequireAuthenticatedUser() fallback policy.
    public override Task<GetSchemaResponse> GetSchema(
        GetSchemaRequest request,
        ServerCallContext context)
    {
        // SchemaCatalogReader owns the filtering. The acting user is passed explicitly rather
        // than resolved inside it, so the admin-console JSON endpoint can hand it HttpContext.User
        // instead — the interceptor that populates IActingUserAccessor is gRPC-only.
        var response = new GetSchemaResponse();
        response.Types_.AddRange(SchemaCatalogReader.BuildCatalog(
            _registry, _authEvaluator, _actingUserAccessor.ActingUser, _logger));

        return Task.FromResult(response);
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    public override async Task<MappingResponse> Get(
        MappingGetRequest request,
        ServerCallContext context)
    {
        _logger.LogInformation("[Mapping.Get] type={Type} key={Key} depth={Depth}",
            request.TypeName.SanitizeForLog(), request.Key.SanitizeForLog(), request.Depth);

        if (request.Depth > _queryLimits.MaxRelationDepth)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Mapping.Get: depth {request.Depth} exceeds the maximum of {_queryLimits.MaxRelationDepth}."));

        var schema = RequireSchema(request.TypeName);

        var rowJson = await FetchByKeyAsync(schema, request.Key,
            EntityAccess.ForTenant(_actingUserAccessor.ActingUser?.FindFirst("tenant_id")?.Value));
        if (rowJson is null)
            return new MappingResponse
            {
                Success = false,
                Error   = $"'{request.TypeName}:{request.Key}' not found.",
                TraceId = request.TraceId
            };

        var entityStruct = JsonParser.Default.Parse<Struct>(rowJson);

        var decision = _authEvaluator.Evaluate(schema, _actingUserAccessor.ActingUser, AuthorizationAction.Read);
        var ownerMismatch  = decision.OwnershipRequired &&
            StructFieldAccess.GetFieldString(entityStruct, decision.OwnerFieldName!) != decision.OwnerValue;
        var tenantMismatch = decision.TenantColumn is not null &&
            StructFieldAccess.GetFieldString(entityStruct, decision.TenantColumn) != decision.TenantValue;
        if (decision.Denied || ownerMismatch || tenantMismatch)
        {
            _auditLog.Denied(_actingUserAccessor.ActingUser, "Read", request.TypeName, request.Key,
                decision.Denied ? "AccessDenied" : ownerMismatch ? "OwnerMismatch" : "TenantMismatch");
            return new MappingResponse
            {
                Success = false,
                Error   = $"'{request.TypeName}:{request.Key}' not found.",
                TraceId = request.TraceId
            };
        }

        AuthorizationFieldMasking.MaskDisallowedFields(entityStruct, decision.AllowedFields);

        if (request.Depth > 0)
            await _relationResolver.ResolveRelationsAsync(
                entityStruct,
                schema,
                request.Depth,
                _actingUserAccessor.ActingUser,
                context.CancellationToken);

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
            AuthorizationAction.Write, "Not authorized to create this entity.", existingRowJson: null, _auditLog,
            _payloadSizeValidator);

        _relationValidator.ValidateAndNormalizeRelations(request.Payload, schema);

        var key = _keyAccessor.AssignNewKey(request.Payload, schema.KeyColumn.Name);

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

        // Strip the server-owned tenant column from the Struct that becomes MappingResponse.Data.
        // EnforceWriteAuthorization force-set it INTO this very object (SetAuthoritativeField ->
        // StructFieldAccess.SetField mutates in place), and `Data = request.Payload` below returns
        // that same object — so without this the column goes back to the caller on every write.
        //
        // AFTER SerializePayload, deliberately. payloadJson is what OutboxPublisher puts on Kafka,
        // and it is the only source of the tenant value for the StarRocks projection
        // (EngagementRepository.UpsertAsync) and the Qdrant point payload
        // (IntelligenceStoreConsumer.BuildObjectPointPayload). Stripping before serialization
        // would leave the StarRocks row's tenant column NULL — StarRocks' Primary Key model
        // treats a partial INSERT as a full-row replace — and every subsequent StarRocks read for
        // that tenant would return nothing. OutboxWriter remains the sole *injector* for the
        // Postgres write; this is only a response-shaping strip.
        AuthorizationFieldMasking.RemoveTenantColumn(request.Payload);

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

        // Narrowed to the acting tenant (CSR round 9 Finding #5) — see the identical read in
        // ObjectPersistenceGrpcService.Update for why a cross-tenant read here was an
        // information-disclosure oracle, and how the RLS collision below replaces it.
        var existingRowJson = await FetchByKeyAsync(schema, key,
            EntityAccess.ForTenant(_actingUserAccessor.ActingUser?.FindFirst("tenant_id")?.Value));
        AuthorizationFieldMasking.EnforceWriteAuthorization(
            _authEvaluator,
            _actingUserAccessor.ActingUser,
            schema,
            request.Payload,
            AuthorizationAction.Write,
            "Not authorized to update this entity.",
            existingRowJson,
            _auditLog,
            _payloadSizeValidator);

        _relationValidator.ValidateAndNormalizeRelations(request.Payload, schema);

        var payloadJson = StructSerializer.SerializePayload(request.Payload);

        var decision = _authEvaluator.Evaluate(schema, _actingUserAccessor.ActingUser, AuthorizationAction.Write);
        Guid outboxRowId;
        try
        {
            outboxRowId = await _outboxWriter.UpsertAndEnqueueOutboxAsync(
                SchemaBuilder.ToTableSchema(schema), request.TypeName, key, payloadJson,
                tenantId: decision.TenantValue);
        }
        catch (PostgresException ex) when (ex.SqlState == "42501" && ex.MessageText.Contains("row-level security policy"))
        {
            _logger.LogWarning(
                "[Mapping.Update] RLS collision swallowed as success: type={Type} key={Key} traceId={TraceId} message={Message}",
                schema.TypeName.SanitizeForLog(), key.SanitizeForLog(), request.TraceId.SanitizeForLog(), ex.MessageText.SanitizeForLog());
            _auditLog.Denied(_actingUserAccessor.ActingUser, "Update", schema.TypeName, key, "BlockedCrossTenantWrite");
            AuthorizationFieldMasking.RemoveTenantColumn(request.Payload);
            return new MappingResponse { Success = true, Data = request.Payload, TraceId = request.TraceId };
        }
        var targetStores = StoreTargeting.DetermineTargetStores(schema);

        // Opportunistic fast-path publish: the durability guarantee already exists (the
        // outbox row committed above, in the same transaction as the entity write), so a
        // failure here is not data loss — the existing ReconciliationQueueWorker (which now
        // polls unconditionally-inserted outbox rows, not just failure-recorded ones — see
        // Task 5's updated ReconciliationSchema doc comment) will pick this row up on its
        // next poll. This just keeps the common case's projection latency low.
        await _outboxPublisher.PublishAsync(
            EntityEventType.Updated,
            request.TypeName,
            key,
            payloadJson,
            request.TraceId,
            targetStores,
            outboxRowId,
            "Mapping.Update",
            priorPayloadJson: existingRowJson);

        // Strip the server-owned tenant column from the Struct that becomes MappingResponse.Data.
        // EnforceWriteAuthorization force-set it INTO this very object (SetAuthoritativeField ->
        // StructFieldAccess.SetField mutates in place), and `Data = request.Payload` below returns
        // that same object — so without this the column goes back to the caller on every write.
        //
        // AFTER SerializePayload, deliberately. payloadJson is what OutboxPublisher puts on Kafka,
        // and it is the only source of the tenant value for the StarRocks projection
        // (EngagementRepository.UpsertAsync) and the Qdrant point payload
        // (IntelligenceStoreConsumer.BuildObjectPointPayload). Stripping before serialization
        // would leave the StarRocks row's tenant column NULL — StarRocks' Primary Key model
        // treats a partial INSERT as a full-row replace — and every subsequent StarRocks read for
        // that tenant would return nothing. OutboxWriter remains the sole *injector* for the
        // Postgres write; this is only a response-shaping strip.
        AuthorizationFieldMasking.RemoveTenantColumn(request.Payload);

        return new MappingResponse { Success = true, Data = request.Payload, TraceId = request.TraceId };
    }

    public override async Task<MappingDeleteResponse> Delete(
        MappingDeleteRequest request, ServerCallContext context)
    {
        _logger.LogInformation("[Mapping.Delete] type={Type} key={Key}", request.TypeName.SanitizeForLog(), request.Key.SanitizeForLog());

        var schema = RequireSchema(request.TypeName);

        var rowJson = await FetchByKeyAsync(schema, request.Key,
            EntityAccess.ForTenant(_actingUserAccessor.ActingUser?.FindFirst("tenant_id")?.Value));
        if (rowJson is null)
            return new MappingDeleteResponse
            {
                Success = false,
                Error   = $"'{request.TypeName}:{request.Key}' not found.",
                TraceId = request.TraceId
            };

        var decision  = _authEvaluator.Evaluate(schema, _actingUserAccessor.ActingUser, AuthorizationAction.Delete);
        var rowStruct = JsonParser.Default.Parse<Struct>(rowJson);
        var ownerMismatch  = decision.OwnershipRequired &&
            StructFieldAccess.GetFieldString(rowStruct, decision.OwnerFieldName!) != decision.OwnerValue;
        var tenantMismatch = decision.TenantColumn is not null &&
            StructFieldAccess.GetFieldString(rowStruct, decision.TenantColumn) != decision.TenantValue;
        if (decision.Denied || ownerMismatch || tenantMismatch)
        {
            _auditLog.Denied(
                _actingUserAccessor.ActingUser,
                "Delete",
                request.TypeName,
                request.Key,
                decision.Denied
                    ? "AccessDenied"
                    : ownerMismatch
                        ? "OwnerMismatch"
                        : "TenantMismatch");
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
                tx,
                SchemaBuilder.ToTableSchema(schema),
                request.Key,
                // A type with no tenant column carries no RLS policy and no iverson_runtime grant,
                // so a tenant-scoped delete of it would be 42501 rather than a filtered delete.
                decision.TenantColumn is not null
                    ? EntityAccess.ForTenant(decision.TenantValue)
                    : EntityAccess.CrossTenantMaintenance);

            await _outboxWriter.EnqueueDeleteOutboxRowAsync(
                tx,
                outboxRowId,
                request.TypeName,
                request.Key,
                rowJson);
        });

        // Opportunistic fast-path publish: the durability guarantee already exists (the
        // delete-outbox row committed above, in the same transaction as the entity delete),
        // so a failure here is not data loss — the existing ReconciliationQueueWorker (which
        // now polls unconditionally-inserted outbox rows, not just failure-recorded ones —
        // see Task 5's updated ReconciliationSchema doc comment) will pick this row up on its
        // next poll and replay it from the stored pre-delete snapshot. This just keeps the
        // common case's projection latency low.
        await _outboxPublisher.PublishAsync(
            EntityEventType.Deleted,
            request.TypeName,
            request.Key,
            rowJson,
            request.TraceId,
            targetStores,
            outboxRowId,
            "Mapping.Delete");

        return new MappingDeleteResponse { Success = true, TraceId = request.TraceId };
    }

    // ── SQL helpers ───────────────────────────────────────────────────────────

    private SchemaDescriptor RequireSchema(string typeName) =>
        _registry.Get(typeName) ?? throw new RpcException(new Status(StatusCode.FailedPrecondition,
            $"No schema registered for '{typeName}'. Call RegisterSchema first."));

    private Task<string?> FetchByKeyAsync(
        SchemaDescriptor schema, string key, EntityAccess access) =>
        _entities.FetchByKeyAsync(
            SchemaBuilder.ToTableSchema(schema),
            key,
            access);
}
