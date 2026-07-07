using Iverson.Sql;

namespace Iverson.Api.Reconciliation;

/// <summary>
/// Table backing the automatic failure-triggered reconciliation queue: one row per entity
/// write whose fire-and-forget Kafka publish failed to deliver. A background worker
/// (<see cref="ReconciliationQueueWorker"/>) polls this table and re-publishes each row via
/// the confirmed <see cref="Iverson.Events.IEventProducer.ProduceAsync{T}"/>, deleting the
/// row once it succeeds. This is internal infrastructure, not a user-registered entity — it
/// is bootstrapped once at startup via the same ApplySchemaAsync mechanism SchemaRegistry
/// uses for user entity tables, not through the schema-registration/proto pipeline.
/// </summary>
internal static class ReconciliationSchema
{
    public const string TableName = "IversonReconciliationQueue";

    public static readonly TableSchema Table = new(
        TableName,
        new ColumnSchema("Id", "uuid", false),
        new List<ColumnSchema>
        {
            new("TypeName", "text", false),
            new("EntityKey", "text", false),
            new("EnqueuedAt", "timestamptz", false),
            new("Attempts", "integer", false),
            new("LastError", "text", true),
            new("LastAttemptAt", "timestamptz", true),
            new("EventType", "text", true),
            new("Payload", "text", true),
        });

    /// <summary>
    /// Enqueues a delete-replay outbox row: unlike the upsert path's inline INSERT (see
    /// <see cref="Iverson.Api.Grpc.ObjectPersistenceGrpcService"/>'s
    /// <c>UpsertAndEnqueueOutboxAsync</c>), a delete leaves no row behind for the reconciliation
    /// worker to re-fetch, so the pre-delete JSON snapshot is captured here as <paramref name="payload"/>
    /// and replayed verbatim by <see cref="ReconciliationService"/> when <c>EventType == "Deleted"</c>.
    /// Must be called from inside the same transaction as the entity DELETE (<paramref name="tx"/>)
    /// so the outbox row's existence is an accurate durability guarantee.
    /// </summary>
    internal static Task EnqueueDeleteOutboxRowAsync(
        IDbTransactionContext tx, Guid id, string typeName, string key, string payload) =>
        tx.ExecuteAsync(
            $"""
            INSERT INTO "{TableName}"
                ("Id", "TypeName", "EntityKey", "EnqueuedAt", "Attempts", "LastError", "LastAttemptAt", "EventType", "Payload")
            VALUES
                (@Id, @TypeName, @EntityKey, @EnqueuedAt, 0, null, null, 'Deleted', @Payload)
            """,
            new
            {
                Id = id,
                TypeName = typeName,
                EntityKey = key,
                EnqueuedAt = DateTimeOffset.UtcNow,
                Payload = payload
            });
}
