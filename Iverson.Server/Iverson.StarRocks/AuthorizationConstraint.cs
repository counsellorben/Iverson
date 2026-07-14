namespace Iverson.StarRocks;

public sealed record AuthorizationConstraint(
    IReadOnlySet<string>? AllowedFields,   // null = unrestricted
    string? OwnerColumn,                    // null = no ownership predicate needed
    string? OwnerValue);
