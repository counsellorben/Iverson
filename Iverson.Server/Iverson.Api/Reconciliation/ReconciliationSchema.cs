using Iverson.Sql;

namespace Iverson.Api.Reconciliation;

/// <summary>
/// The transactional outbox: one row per entity write, inserted in the same Postgres
/// transaction as the entity upsert or delete itself (<see cref="Iverson.Api.Grpc.ObjectPersistenceGrpcService"/>,
/// <see cref="Iverson.Api.Grpc.ObjectMappingGrpcService"/>), so the row's existence is the
/// durability guarantee that the write will eventually reach StarRocks/Qdrant — independent of
/// whether the write path's own opportunistic Kafka publish attempt succeeds, fails, or never
/// runs because the process crashes first. A background worker (<see cref="ReconciliationQueueWorker"/>)
/// polls for rows still present and re-publishes each via <see cref="Iverson.Events.IEventProducer.ProduceAsync{T}"/>,
/// deleting the row once it succeeds — this is the ONLY path an event is guaranteed to
/// eventually take if every opportunistic fast-path attempt fails. Upsert rows (<c>EventType</c>
/// null) are replayed by re-reading the entity's current state from Postgres at replay time;
/// delete rows (<c>EventType = "Deleted"</c>) are replayed from the <c>Payload</c> column's
/// stored pre-delete snapshot, since the entity row no longer exists by the time the worker
/// polls — see <see cref="Iverson.Sql.IOutboxWriter.EnqueueDeleteOutboxRowAsync"/> for how a
/// delete-replay row is enqueued (it must run inside the same transaction as the entity DELETE
/// so the outbox row's existence is an accurate durability guarantee). The same table and worker
/// also serve manual/admin full-type reconciliation (see
/// <see cref="ReconciliationService.ReconcileTypeAsync"/>). This is internal infrastructure, not
/// a user-registered entity — it is bootstrapped once at startup via the same ApplySchemaAsync
/// mechanism SchemaRegistry uses for user entity tables, not through the schema-registration/
/// proto pipeline.
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
}
