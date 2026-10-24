using System.Security.Claims;

namespace Iverson.Api;

public static class SchemaAdminAuthorizationPolicy
{
    // Operator implies SchemaAdmin (CSR round 12 §2.4): the operator list, the `admin` scope and compose's
    // group fallback all satisfy it, through the one Operator function. Automation that should register
    // schemas without becoming an Operator gets the narrower `schema_admin` scope instead, which would not
    // also grant /admin/dlq and /admin/reconcile.
    public static bool IsSatisfiedBy(ClaimsPrincipal principal, OperatorSubjects operatorSubjects)
    {
        if (OperatorAuthorizationPolicy.IsSatisfiedBy(principal, operatorSubjects))
            return true;

        return principal.FindFirst("scope")?.Value is { } scope
            && scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("schema_admin");
    }
}
