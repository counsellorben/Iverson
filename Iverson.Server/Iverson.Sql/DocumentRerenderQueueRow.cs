namespace Iverson.Sql;

public sealed record DocumentRerenderQueueRow(
    Guid Id,
    string? TenantId,
    string TypeName,
    string? EntityKey,
    string? Cursor,
    int Attempts);
