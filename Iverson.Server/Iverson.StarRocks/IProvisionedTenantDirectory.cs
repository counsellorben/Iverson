namespace Iverson.StarRocks;

/// <summary>
/// CSR round 12 §7.3: which tenants have a StarRocks login. The worker records a tenant once provisioning it
/// succeeds, so a recorded tenant's login exists; every StarRocks reader asks before connecting and answers an
/// unrecorded tenant as empty. This project references no Postgres project, so the record is injected:
/// Iverson.Api implements it over the <c>IversonStarRocksTenants</c> table.
/// </summary>
public interface IProvisionedTenantDirectory
{
    /// <summary>True once the tenant has been recorded as provisioned.</summary>
    Task<bool> IsProvisionedAsync(string tenantId);

    /// <summary>Records the tenant as provisioned. Idempotent. Called only after the tenant's login exists.</summary>
    Task RecordProvisionedAsync(string tenantId);
}
