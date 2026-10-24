using System.Security.Claims;

namespace Iverson.Api;

// CSR round 12 §2.2: the console is for admins only. ConsoleAdmin is Operator or TenantAdmin, through the
// same two functions the Operator and TenantAdmin policies use, so the console's /admin/console/roles answer
// and the server's own decisions cannot disagree.
public static class ConsoleAdminAuthorizationPolicy
{
    public static bool IsSatisfiedBy(ClaimsPrincipal principal, OperatorSubjects operatorSubjects) =>
        OperatorAuthorizationPolicy.IsSatisfiedBy(principal, operatorSubjects)
        || TenantAdminAuthorizationPolicy.IsSatisfiedBy(principal.FindAll("groups").Select(c => c.Value));
}
