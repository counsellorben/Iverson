namespace Iverson.Api;

public static class OperatorAuthorizationPolicy
{
    // Two caller shapes satisfy "operator": a human via Authentik group membership (groups
    // claim, exploded into one Claim per array element by the JWT handler — confirmed via a
    // real decoded token), or CI/runbook automation via a dedicated `admin` scope (a single
    // space-separated string claim, not an array — confirmed via a real decoded token).
    public static bool IsSatisfiedBy(IEnumerable<string> groupClaims, string? scopeClaim)
    {
        if (groupClaims.Contains("operators"))
            return true;

        return scopeClaim is not null && scopeClaim.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("admin");
    }
}
