using System.Security.Claims;

namespace Iverson.Api.Tests.Helpers;

public static class ActingUserFixtures
{
    // Every SchemaFixtures descriptor declares TenantColumn = "TenantId" (Task 1's mandatory
    // tenant_field cutover), so a real evaluator now denies any principal with no tenant_id
    // claim. Default every minted principal to the same tenant, matching SchemaFixtures'
    // hardcoded convention, so existing call sites keep passing without individual changes.
    public static ClaimsPrincipal Principal(string sub, params string[] groups) =>
        PrincipalWithTenant(sub, "test-tenant", groups);

    public static ClaimsPrincipal PrincipalWithTenant(string sub, string? tenantId, params string[] groups)
    {
        var claims = new List<Claim> { new("sub", sub) };
        if (tenantId is not null)
            claims.Add(new("tenant_id", tenantId));
        claims.AddRange(groups.Select(g => new Claim("groups", g)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }
}
