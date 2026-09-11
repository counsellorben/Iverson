namespace Iverson.Api.Tenancy;

public sealed record IdpUser(string Id, string Username, string Email);

public interface IIdpAdminClient
{
    // CSR finding #4 remediation: no password parameter here by design. The platform never
    // transmits a user's password to Authentik — see IdpAdminClient.CreateUserAsync, which
    // creates the user and then triggers Authentik's own recovery-link flow so the user sets
    // their own password directly against Authentik.
    Task<string> CreateUserAsync(string username, string email, string tenantId, IReadOnlyList<string> groups);
    Task<IEnumerable<IdpUser>> ListUsersByTenantAsync(string tenantId);
    Task DeactivateUserAsync(string userId);
    Task DeactivateAllUsersInTenantAsync(string tenantId);
    Task AddGroupAsync(string userId, string groupName);
    Task RemoveGroupAsync(string userId, string groupName);
}
