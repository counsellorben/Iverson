namespace Iverson.Api.Tenancy;

public interface ITenantStatusCache
{
    Task<string?> GetStatusAsync(string tenantId);
}
