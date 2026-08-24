using Grpc.Core;
using Iverson.Api.Authorization;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Events;
using Iverson.Sql;

namespace Iverson.Api.Grpc;

/// <summary>
/// Lightweight write path. Assigns the server-generated UUID v7 key on create — a client
/// never assigns an ID, and a payload that already carries one is rejected — writes directly
/// to Postgres, then publishes an EntityEvent for StarRocks and Qdrant to consume via their
/// consumer groups.
/// </summary>
public sealed class ObjectPersistenceGrpcService(
    IOutboxPublisher outboxPublisher,
    SchemaRegistry registry,
    IRelationValidator relationValidator,
    IPayloadSizeValidator payloadSizeValidator,
    IEntityKeyAccessor keyAccessor,
    IOutboxWriter outboxWriter,
    ILogger<ObjectPersistenceGrpcService> logger,
    IEntityRepository entities,
    IActingUserAccessor actingUserAccessor,
    IRowFieldAuthorizationEvaluator authEvaluator,
    AuditLog auditLog)
    : ObjectPersistenceService.ObjectPersistenceServiceBase
{
    public override async Task<PersistResponse> Post(
        PersistRequest request, ServerCallContext context)
    {
        var schema = RequireSchema(request.TypeName);

        AuthorizationFieldMasking.EnforceWriteAuthorization(
            authEvaluator,
            actingUserAccessor.ActingUser,
            schema,
            request.Payload,
            AuthorizationAction.Write,
            "Not authorized to create this entity.",
            existingRowJson: null,
            auditLog);

        relationValidator.ValidateAndNormalizeRelations(request.Payload, schema);
        payloadSizeValidator.ValidateTextColumnSizes(request.Payload, schema);

        var targetStores = StoreTargeting.DetermineTargetStores(schema);

        var key = keyAccessor.AssignNewKey(request.Payload, schema.KeyColumn.Name);

        var payloadJson = StructSerializer.SerializePayload(request.Payload);

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("[Persistence.Post] type={Type} key={Key} stores={Stores}",
                request.TypeName.SanitizeForLog(), key, targetStores);

        var decision = authEvaluator.Evaluate(
            schema,
            actingUserAccessor.ActingUser,
            AuthorizationAction.Write);
        var outboxRowId = await outboxWriter.UpsertAndEnqueueOutboxAsync(
            SchemaBuilder.ToTableSchema(schema),
            request.TypeName,
            key,
            payloadJson,
            tenantId: decision.TenantValue);

        // Opportunistic fast-path publish: the durability guarantee already exists (the
        // outbox row committed above, in the same transaction as the entity write), so a
        // failure here is not data loss — the existing ReconciliationQueueWorker (which now
        // polls unconditionally-inserted outbox rows, not just failure-recorded ones — see
        // Task 5's updated ReconciliationSchema doc comment) will pick this row up on its
        // next poll. This just keeps the common case's projection latency low.
        await outboxPublisher.PublishAsync(
            EntityEventType.Created,
            request.TypeName,
            key,
            payloadJson,
            request.TraceId,
            targetStores,
            outboxRowId,
            "Persistence.Post");

        return new PersistResponse
        {
            Success = true,
            Key     = key,
            TraceId = request.TraceId
        };
    }

    public override async Task<PersistResponse> Update(
        PersistRequest request,
        ServerCallContext context)
    {
        var schema = RequireSchema(request.TypeName);

        var key = keyAccessor.ExtractKey(request.Payload, schema.KeyColumn.Name);
        if (string.IsNullOrWhiteSpace(key) || key == Guid.Empty.ToString())
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Update requires a non-empty '{schema.KeyColumn.Name}' in the payload."));

        var existingRowJson = await entities.FetchByKeyAsync(SchemaBuilder.ToTableSchema(schema), key);
        AuthorizationFieldMasking.EnforceWriteAuthorization(
            authEvaluator,
            actingUserAccessor.ActingUser,
            schema,
            request.Payload,
            AuthorizationAction.Write,
            "Not authorized to update this entity.",
            existingRowJson,
            auditLog);

        relationValidator.ValidateAndNormalizeRelations(request.Payload, schema);
        payloadSizeValidator.ValidateTextColumnSizes(request.Payload, schema);

        var targetStores = StoreTargeting.DetermineTargetStores(schema);

        var payloadJson = StructSerializer.SerializePayload(request.Payload);

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("[Persistence.Update] type={Type} key={Key} stores={Stores}",
                request.TypeName.SanitizeForLog(), key, targetStores);

        var decision = authEvaluator.Evaluate(schema, actingUserAccessor.ActingUser, AuthorizationAction.Write);
        var outboxRowId = await outboxWriter.UpsertAndEnqueueOutboxAsync(
            SchemaBuilder.ToTableSchema(schema),
            request.TypeName,
            key,
            payloadJson,
            tenantId: decision.TenantValue);

        // Opportunistic fast-path publish: the durability guarantee already exists (the
        // outbox row committed above, in the same transaction as the entity write), so a
        // failure here is not data loss — the existing ReconciliationQueueWorker (which now
        // polls unconditionally-inserted outbox rows, not just failure-recorded ones — see
        // Task 5's updated ReconciliationSchema doc comment) will pick this row up on its
        // next poll. This just keeps the common case's projection latency low.
        await outboxPublisher.PublishAsync(
            EntityEventType.Updated,
            request.TypeName,
            key,
            payloadJson,
            request.TraceId,
            targetStores,
            outboxRowId,
            "Persistence.Update",
            priorPayloadJson: existingRowJson);

        return new PersistResponse
        {
            Success = true,
            Key     = key,
            TraceId = request.TraceId
        };
    }

    private SchemaDescriptor RequireSchema(string typeName) =>
        registry.Get(typeName) ?? throw new RpcException(
            new Status(StatusCode.FailedPrecondition,
            $"No schema registered for '{typeName}'. Call RegisterSchema first."));
}
