namespace Iverson.Sql;

/// <summary>
/// CSR round 12 §2.1: InviteUser's limit of 100 invitations per tenant per UTC day. One row per
/// tenant per day that has had an invitation, counting the slots reserved that day; rows are not
/// pruned. The worker creates the table at startup (<c>WorkerStoreBootstrap</c>). The api reserves a
/// slot with one upsert before it calls Authentik, so api replicas sharing the database hand out at
/// most 100 between them, and a slot is not returned if the IdP call then fails.
/// </summary>
public sealed class InviteQuotaRepository(IRecordStoreQueryExecutor sql) : IInviteQuotaRepository
{
    public const string TableName = "IversonInviteQuota";

    // Raw DDL, like the token revocations: ApplySchemaAsync cannot express a composite primary key.
    public Task EnsureTableAsync() =>
        sql.ExecuteAsync(StartupDdl.Serialised(TableName,
            $"""
            CREATE TABLE IF NOT EXISTS "{TableName}" (
                "TenantId" text,
                "Day"      date,
                "Count"    int,
                PRIMARY KEY ("TenantId", "Day")
            )
            """));

    // No row back means the day's 100 are spent: the conflicting row's WHERE refused the update.
    public async Task<bool> TryReserveAsync(string tenantId, DateOnly day) =>
        await sql.QuerySingleOrDefaultAsync<int?>(
            $"""
            INSERT INTO "{TableName}" ("TenantId", "Day", "Count")
            VALUES (@TenantId, @Day, 1)
            ON CONFLICT ("TenantId", "Day") DO UPDATE
                SET "Count" = "{TableName}"."Count" + 1
                WHERE "{TableName}"."Count" < 100
            RETURNING "Count"
            """,
            new { TenantId = tenantId, Day = day }) is not null;
}
