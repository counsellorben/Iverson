using System.Diagnostics;
using Grpc.Core;
using Iverson.Api.Reconciliation;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Events;
using Iverson.Sql;

namespace Iverson.Api.Grpc;

/// <summary>
/// Lightweight write path. Stamps a server-generated UUID v7 key when the
/// client sends an empty key, writes directly to Postgres, then publishes
/// an EntityEvent for StarRocks and Qdrant to consume via their consumer groups.
/// </summary>
public sealed class ObjectPersistenceGrpcService(
    IEventProducer events,
    SchemaRegistry registry,
    IRelationValidator relationValidator,
    IEntityKeyAccessor keyAccessor,
    IOutboxWriter outboxWriter,
    ILogger<ObjectPersistenceGrpcService> logger)
    : ObjectPersistenceService.ObjectPersistenceServiceBase
{
    private const string SchemaVersion = "1";

    public override async Task<PersistResponse> Post(
        PersistRequest request, ServerCallContext context)
    {
        var schema = RequireSchema(request.TypeName);

        relationValidator.ValidateRelations(request.Payload, schema);

        var targetStores = StoreTargeting.DetermineTargetStores(schema);

        // Always assign a server-generated UUID v7; client-supplied Id is ignored
        var key = Guid.CreateVersion7().ToString();
        keyAccessor.SetKey(request.Payload, schema.KeyColumn.Name, key);

        var payloadJson = StructSerializer.SerializePayload(request.Payload);

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("[Persistence.Post] type={Type} key={Key} stores={Stores}",
                request.TypeName, key, targetStores);

        var outboxRowId = await outboxWriter.UpsertAndEnqueueOutboxAsync(SchemaBuilder.ToTableSchema(schema), request.TypeName, key, payloadJson);

        // Opportunistic fast-path publish: the durability guarantee already exists (the
        // outbox row committed above, in the same transaction as the entity write), so a
        // failure here is not data loss — the existing ReconciliationQueueWorker (which now
        // polls unconditionally-inserted outbox rows, not just failure-recorded ones — see
        // Task 5's updated ReconciliationSchema doc comment) will pick this row up on its
        // next poll. This just keeps the common case's projection latency low.
        var published = false;
        try
        {
            await events.ProduceAsync(
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
            await outboxWriter.DeleteOutboxRowIfPresentAsync(outboxRowId);
        }
        catch (Exception ex) when (!published)
        {
            logger.LogWarning(ex,
                "[Persistence.Post] Opportunistic publish failed for type={Type} key={Key} — " +
                "ReconciliationQueueWorker will retry from the durable outbox row", request.TypeName, key);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[Persistence.Post] Publish succeeded but outbox cleanup failed for type={Type} key={Key} — " +
                "ReconciliationQueueWorker will harmlessly re-publish from the durable outbox row", request.TypeName, key);
        }

        return new PersistResponse
        {
            Success = true,
            Key     = key,
            TraceId = request.TraceId
        };
    }

    public override async Task<PersistResponse> Update(
        PersistRequest request, ServerCallContext context)
    {
        var schema = RequireSchema(request.TypeName);

        var key = keyAccessor.ExtractKey(request.Payload, schema.KeyColumn.Name);
        if (string.IsNullOrWhiteSpace(key) || key == Guid.Empty.ToString())
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"Update requires a non-empty '{schema.KeyColumn.Name}' in the payload."));

        relationValidator.ValidateRelations(request.Payload, schema);

        var targetStores = StoreTargeting.DetermineTargetStores(schema);

        var payloadJson = StructSerializer.SerializePayload(request.Payload);

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("[Persistence.Update] type={Type} key={Key} stores={Stores}",
                request.TypeName, key, targetStores);

        var outboxRowId = await outboxWriter.UpsertAndEnqueueOutboxAsync(SchemaBuilder.ToTableSchema(schema), request.TypeName, key, payloadJson);

        // Opportunistic fast-path publish: the durability guarantee already exists (the
        // outbox row committed above, in the same transaction as the entity write), so a
        // failure here is not data loss — the existing ReconciliationQueueWorker (which now
        // polls unconditionally-inserted outbox rows, not just failure-recorded ones — see
        // Task 5's updated ReconciliationSchema doc comment) will pick this row up on its
        // next poll. This just keeps the common case's projection latency low.
        var published = false;
        try
        {
            await events.ProduceAsync(
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
            await outboxWriter.DeleteOutboxRowIfPresentAsync(outboxRowId);
        }
        catch (Exception ex) when (!published)
        {
            logger.LogWarning(ex,
                "[Persistence.Update] Opportunistic publish failed for type={Type} key={Key} — " +
                "ReconciliationQueueWorker will retry from the durable outbox row", request.TypeName, key);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[Persistence.Update] Publish succeeded but outbox cleanup failed for type={Type} key={Key} — " +
                "ReconciliationQueueWorker will harmlessly re-publish from the durable outbox row", request.TypeName, key);
        }

        return new PersistResponse
        {
            Success = true,
            Key     = key,
            TraceId = request.TraceId
        };
    }

    private SchemaDescriptor RequireSchema(string typeName) =>
        registry.Get(typeName) ?? throw new RpcException(new Status(StatusCode.FailedPrecondition,
            $"No schema registered for '{typeName}'. Call RegisterSchema first."));
}

file static class StringExtensions
{
    internal static string? NullIfEmpty(this string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;
}
