namespace Iverson.Api.Tenancy;

public sealed record IdpUser(string Id, string Username, string Email);

public interface IIdpAdminClient
{
    Task<string> CreateUserAsync(string username, string email, string password, string tenantId, IReadOnlyList<string> groups);
    Task<IEnumerable<IdpUser>> ListUsersByTenantAsync(string tenantId);
    Task DeactivateUserAsync(string userId);
    Task DeactivateAllUsersInTenantAsync(string tenantId);
    Task AddGroupAsync(string userId, string groupName);
    Task RemoveGroupAsync(string userId, string groupName);
}
