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
        });
}
