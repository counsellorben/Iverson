namespace Iverson.Api;

public static class TenantAdminAuthorizationPolicy
{
    public static bool IsSatisfiedBy(IEnumerable<string> groupClaims) => groupClaims.Contains("tenant-admins");
}
