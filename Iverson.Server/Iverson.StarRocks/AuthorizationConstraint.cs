namespace Iverson.StarRocks;

public sealed record AuthorizationConstraint(
    IReadOnlySet<string>? AllowedFields,   // null = unrestricted
    string? OwnerColumn,                    // null = no ownership predicate needed
    string? OwnerValue,
    string? TenantColumn = null,            // null only in tests that predate the tenant boundary
    string? TenantValue = null);
