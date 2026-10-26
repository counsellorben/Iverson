namespace Iverson.Api.Tenancy;

public interface ITenantStatusCache
{
    Task<string?> GetStatusAsync(string tenantId);

    /// <summary>
    /// Drops this process's cached status for <paramref name="tenantId"/>, so the next
    /// <see cref="GetStatusAsync"/> reads the repository. Other replicas are not reached.
    /// </summary>
    void Invalidate(string tenantId);

    /// <summary>
    /// Drops this process's cached status for every tenant (CSR round 12 §2.3).
    /// <see cref="AuthInvalidationListener"/> calls it on every notification and on every connect.
    /// A read already in flight does not repopulate what this dropped.
    /// </summary>
    void InvalidateAll();
}
