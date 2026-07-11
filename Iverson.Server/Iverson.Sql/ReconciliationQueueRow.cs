namespace Iverson.Sql;

public sealed record ReconciliationQueueRow(
    Guid Id, string TypeName, string EntityKey, int Attempts, string? EventType = null, string? Payload = null);
