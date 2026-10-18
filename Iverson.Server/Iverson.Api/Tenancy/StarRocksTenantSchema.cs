using Iverson.Sql;

namespace Iverson.Api.Tenancy;

/// <summary>
/// CSR round 12 §7.3: the record of which tenants have a StarRocks login. The worker creates the table at
/// startup (<see cref="WorkerStoreBootstrap"/>) and inserts a row after each successful provisioning; every
/// StarRocks reader consults it through <see cref="PostgresProvisionedTenantDirectory"/>. It has no tenant
/// column, so no RLS: the api's login reads it through its one grant (<see cref="ApiLoginGrants"/>) and never
/// writes it.
/// </summary>
internal static class StarRocksTenantSchema
{
    public const string TableName = "IversonStarRocksTenants";

    public static readonly TableSchema Table = new(
        TableName,
        new ColumnSchema("TenantId", "text", false),
        new List<ColumnSchema>
        {
            new("ProvisionedAt", "timestamptz", false),
        });
}
