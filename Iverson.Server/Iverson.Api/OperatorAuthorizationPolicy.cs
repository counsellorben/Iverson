using System.Security.Claims;

namespace Iverson.Api;

public static class OperatorAuthorizationPolicy
{
    // CSR round 12 §2.4. Three caller shapes satisfy "operator": a human whose `sub` is on this install's
    // operator list; CI/runbook automation via a dedicated `admin` scope (a single space-separated string
    // claim, not an array — confirmed via a real decoded token); and, on compose only, a member of the
    // dev fallback group. The `groups` claim (one Claim per array element) yields Operator nowhere else:
    // the orchestrator can place users in groups, so a group must not be what makes an Operator.
    // Used by the Operator policy, the three hand-written acting-user checks in Program.cs,
    // SchemaAdminAuthorizationPolicy, ConsoleAdminAuthorizationPolicy and /admin/console/roles.
    public static bool IsSatisfiedBy(ClaimsPrincipal principal, OperatorSubjects operatorSubjects)
    {
        if (principal.FindFirst("sub")?.Value is { } sub && operatorSubjects.Subjects.Contains(sub))
            return true;

        if (principal.FindFirst("scope")?.Value is { } scope
            && scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("admin"))
            return true;

        return operatorSubjects.GroupFallback is { } fallbackGroup
            && principal.FindAll("groups").Any(c => c.Value == fallbackGroup);
    }
}
