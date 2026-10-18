namespace Iverson.Sql;

/// <summary>
/// The queue through which the api hands privileged work to the worker (CSR round 12 §5):
/// RegisterSchema's DDL and registry write, <c>/admin/reconcile/{type}</c> and DLQ replay. The api
/// inserts a row and polls its status; the worker claims it, runs it and records the outcome.
/// A plumbing table with no tenant column, so no RLS policy.
/// </summary>
public sealed class PrivilegedRequestRepository(
    string tableName,
    IRecordStoreQueryExecutor sql) : IPrivilegedRequestRepository
{
    // RequestedAt and ExpiresAt come from the database clock, the clock the claim's expiry test
    // reads, so the api's and the worker's clocks never have to agree. A null expiry leaves
    // ExpiresAt NULL (NULL * interval is NULL): the request never expires.
    public Task InsertAsync(Guid id, string kind, string payloadJson, string? requestedBy, TimeSpan? expiresAfter) =>
        sql.ExecuteAsync(
            $"""
            INSERT INTO "{tableName}" ("Id", "Kind", "Payload", "RequestedBy", "Status", "RequestedAt", "ExpiresAt")
            VALUES (@Id, @Kind, @Payload::jsonb, @RequestedBy, '{PrivilegedRequestStatus.Pending}', now(),
                    now() + @ExpiresAfterSeconds * interval '1 second')
            """,
            new
            {
                Id = id,
                Kind = kind,
                Payload = payloadJson,
                RequestedBy = requestedBy,
                ExpiresAfterSeconds = expiresAfter?.TotalSeconds
            });

    public Task<PrivilegedRequestRow?> GetAsync(Guid id) =>
        sql.QuerySingleOrDefaultAsync<PrivilegedRequestRow>(
            $"""
            SELECT "Id", "Kind", "Payload", "Status", "ResultJson"
            FROM "{tableName}"
            WHERE "Id" = @Id
            """,
            new { Id = id });

    // Sets only Status, which is all the api's column grant allows (§6.2). A request the worker
    // has already claimed is 'running', so this matches no row and the claim stands.
    public async Task<bool> ExpireIfPendingAsync(Guid id) =>
        await sql.ExecuteAsync(
            $"""
            UPDATE "{tableName}" SET "Status" = '{PrivilegedRequestStatus.Expired}'
            WHERE "Id" = @Id AND "Status" = '{PrivilegedRequestStatus.Pending}'
            """,
            new { Id = id }) == 1;

    public Task<IEnumerable<PrivilegedRequestRow>> ListClaimableAsync(int limit) =>
        sql.QueryAsync<PrivilegedRequestRow>(
            $"""
            SELECT "Id", "Kind", "Payload", "Status", "ResultJson"
            FROM "{tableName}"
            WHERE "Status" = '{PrivilegedRequestStatus.Pending}'
              AND ("ExpiresAt" IS NULL OR "ExpiresAt" > now())
            ORDER BY "RequestedAt"
            LIMIT @Limit
            """,
            new { Limit = limit });

    // False when another worker replica claimed it first, the api expired it, or its expiry passed.
    public async Task<bool> TryClaimAsync(Guid id) =>
        await sql.ExecuteAsync(
            $"""
            UPDATE "{tableName}" SET "Status" = '{PrivilegedRequestStatus.Running}', "ClaimedAt" = now()
            WHERE "Id" = @Id AND "Status" = '{PrivilegedRequestStatus.Pending}'
              AND ("ExpiresAt" IS NULL OR "ExpiresAt" > now())
            """,
            new { Id = id }) == 1;

    public Task CompleteAsync(Guid id, string status, string resultJson) =>
        sql.ExecuteAsync(
            $"""
            UPDATE "{tableName}"
            SET "Status" = @Status, "ResultJson" = @ResultJson::jsonb, "CompletedAt" = now()
            WHERE "Id" = @Id
            """,
            new { Id = id, Status = status, ResultJson = resultJson });

    // A request claimed before the cutoff and still 'running' belongs to a worker that stopped
    // without recording an outcome. A NULL ClaimedAt (never claimed) matches nothing. A request
    // that is in fact still running past the cutoff is marked too; its own CompleteAsync, which
    // matches on "Id" alone, then records the real outcome over this one.
    public Task<int> FailAbandonedAsync(TimeSpan claimedBefore, string resultJson) =>
        sql.ExecuteAsync(
            $"""
            UPDATE "{tableName}"
            SET "Status" = '{PrivilegedRequestStatus.Failed}', "ResultJson" = @ResultJson::jsonb, "CompletedAt" = now()
            WHERE "Status" = '{PrivilegedRequestStatus.Running}'
              AND "ClaimedAt" < now() - @AgeSeconds * interval '1 second'
            """,
            new { ResultJson = resultJson, AgeSeconds = claimedBefore.TotalSeconds });

    // Finished means succeeded, failed or expired, and also a pending request whose expiry has
    // passed: nothing can claim it any more, because its api gave up without marking it.
    public Task<int> DeleteFinishedOlderThanAsync(TimeSpan age) =>
        sql.ExecuteAsync(
            $"""
            DELETE FROM "{tableName}"
            WHERE "RequestedAt" < now() - @AgeSeconds * interval '1 second'
              AND ("Status" IN ('{PrivilegedRequestStatus.Succeeded}', '{PrivilegedRequestStatus.Failed}', '{PrivilegedRequestStatus.Expired}')
                   OR ("Status" = '{PrivilegedRequestStatus.Pending}' AND "ExpiresAt" < now()))
            """,
            new { AgeSeconds = age.TotalSeconds });
}
