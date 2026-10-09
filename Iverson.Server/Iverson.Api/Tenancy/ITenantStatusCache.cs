namespace Iverson.Api.Tenancy;

public interface ITenantStatusCache
{
    Task<string?> GetStatusAsync(string tenantId);

    /// <summary>
    /// Drops this process's cached status for <paramref name="tenantId"/>, so the next
    /// <see cref="GetStatusAsync"/> reads the repository. Other replicas are not reached.
    /// </summary>
    void Invalidate(string tenantId);
}
