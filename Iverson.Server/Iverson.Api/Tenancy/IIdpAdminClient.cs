namespace Iverson.Api.Tenancy;

public sealed record IdpUser(string Id, string Username, string Email);

/// <summary>
/// Result of creating a new IdP user via the recovery-link onboarding flow (CSR round-2 finding
/// #4). <see cref="RecoveryLink"/> is the one-time link the user follows to set their own
/// password directly against Authentik — null when Authentik's recovery-link response didn't
/// contain one (logged as a warning by <see cref="IdpAdminClient"/>; the user was still created).
/// </summary>
public sealed record CreateUserResult(string UserId, string? RecoveryLink);

public interface IIdpAdminClient
{
    // CSR finding #4 remediation: no password parameter here by design. The platform never
    // transmits a user's password to Authentik — see IdpAdminClient.CreateUserAsync, which
    // creates the user and then triggers Authentik's own recovery-link flow so the user sets
    // their own password directly against Authentik. The link is returned (CreateUserResult),
    // not only logged, so callers can surface it in the gRPC response (TenantUser.recovery_link /
    // Tenant.admin_recovery_link).
    Task<CreateUserResult> CreateUserAsync(string username, string email, string tenantId, IReadOnlyList<string> groups);
    Task<IEnumerable<IdpUser>> ListUsersByTenantAsync(string tenantId);
    Task DeactivateUserAsync(string userId);
    Task DeactivateAllUsersInTenantAsync(string tenantId);
    Task AddGroupAsync(string userId, string groupName);
    Task RemoveGroupAsync(string userId, string groupName);
}
