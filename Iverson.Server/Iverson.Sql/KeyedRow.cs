namespace Iverson.Sql;

public sealed record KeyedRow(string Key, string Data);

public sealed record KeyedTenantRow(string Key, string? TenantId);
