namespace Iverson.Api;

public static class SchemaAdminAuthorizationPolicy
{
    // Narrower than OperatorAuthorizationPolicy: operators already have this right (they're
    // more trusted than schema registration requires, so no separate scope needed for them),
    // but automation callers get it via a dedicated `schema_admin` scope rather than the
    // broader `admin` scope — reusing `admin` here would also grant those callers
    // /admin/dlq and /admin/reconcile access, which is more than schema registration needs.
    public static bool IsSatisfiedBy(IEnumerable<string> groupClaims, string? scopeClaim)
    {
        if (groupClaims.Contains("operators"))
            return true;

        return scopeClaim is not null && scopeClaim.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("schema_admin");
    }
}
