using Iverson.Sql;

namespace Iverson.Api.Reconciliation;

/// <summary>
/// Table recording every message routed to the Kafka DLQ topic (<see cref="Iverson.Events.EntityTopics.Dlq"/>).
/// Populated by <see cref="DlqMonitorConsumer"/>; read and replayed by the
/// <c>/admin/dlq</c> endpoints. Bootstrapped the same way as <see cref="ReconciliationSchema"/>.
/// </summary>
internal static class DlqSchema
{
    public const string TableName = "IversonDlqMessages";

    public static readonly TableSchema Table = new(
        TableName,
        new ColumnSchema("Id", "uuid", false),
        new List<ColumnSchema>
        {
            new("SourceTopic",      "text", false),
            new("ConsumerGroup",    "text", false),
            new("MessageKey",       "text", false),
            new("MessageValue",     "text", false),
            new("ExceptionType",    "text", true),
            new("ExceptionMessage", "text", true),
            new("Attempts",         "integer", false),
            new("FailedAt",         "timestamptz", false),
            new("Replayed",         "boolean", false),
        });
}
