namespace Iverson.Api.Tenancy;

public sealed record AuthentikUser(string Id, string Username, string Email);

public interface IAuthentikAdminClient
{
    Task<string> CreateUserAsync(string username, string email, string password, string tenantId, IReadOnlyList<string> groups);
    Task<IEnumerable<AuthentikUser>> ListUsersByTenantAsync(string tenantId);
    Task DeactivateUserAsync(string userId);
    Task DeactivateAllUsersInTenantAsync(string tenantId);
    Task AddGroupAsync(string userId, string groupName);
    Task RemoveGroupAsync(string userId, string groupName);
}
