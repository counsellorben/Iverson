using Iverson.Api.Reconciliation;
using Iverson.Api.Tenancy;
using Iverson.Sql;

namespace Iverson.Api;

/// <summary>
/// The worker's startup DDL (CSR round 12 §6.3). Only the worker runs it: it connects as the
/// database owner, while the api's login owns nothing and may create nothing. It creates every
/// plumbing table, ensures the two entity roles, and then gives the api's login exactly
/// <see cref="ApiLoginGrants.Statements"/>, so the grants always follow the tables they name.
/// Program.cs calls it, and so does the integration fixture that proves the grants.
/// </summary>
internal static class WorkerStoreBootstrap
{
    public static async Task RunAsync(
        IRecordStoreSchemaManager schemaManager,
        ISchemaRegistryRepository schemaRegistry,
        IEnrichmentStateRepository enrichmentState,
        IDocumentRerenderQueueRepository rerenderQueue,
        ITokenRevocationRepository tokenRevocations,
        string? apiLoginPassword,
        TimeSpan apiLoginWait)
    {
        // The schema registry's table. SchemaRegistry.LoadAsync no longer creates it: the api loads
        // the registry at startup and every 30 s, and may not run DDL.
        await schemaRegistry.EnsureTableAsync();

        // The enrichment loop breaker's table.
        await enrichmentState.EnsureTableAsync();

        // The document re-render queue: raw DDL, because it needs the two partial unique indexes
        // ApplySchemaAsync cannot express.
        await rerenderQueue.EnsureTableAsync();

        // Token revocations (CSR round-10 #11): raw DDL, the same shape.
        await tokenRevocations.EnsureTableAsync();

        // EnsureRolesAsync must run before ANY ApplySchemaAsync call, since that DDL GRANTs to
        // iverson_maintenance on every table (and to iverson_runtime on the tenant-scoped ones) —
        // both roles have to exist first.
        await schemaManager.EnsureRolesAsync();
        await schemaManager.ApplySchemaAsync(ReconciliationSchema.Table);
        await schemaManager.ApplySchemaAsync(DlqSchema.Table);
        await schemaManager.ApplySchemaAsync(TenantSchema.Table);

        // CSR round 12 §7.3: which tenants have a StarRocks login. Before the grants below, which name it.
        await schemaManager.ApplySchemaAsync(StarRocksTenantSchema.Table);
        await schemaManager.ApplySchemaAsync(Iverson.Api.PrivilegedRequests.PrivilegedRequestSchema.Table);

        // Last, once every table the grants name exists (CSR round 12 §6.2).
        await schemaManager.EnsureApiLoginAsync(apiLoginPassword, ApiLoginGrants.Statements, apiLoginWait);
    }
}
