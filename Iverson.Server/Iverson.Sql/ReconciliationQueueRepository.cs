namespace Iverson.Sql;

public sealed class ReconciliationQueueRepository(
    string tableName,
    IRecordStoreQueryExecutor sql) : IReconciliationQueueRepository
{
    public Task<IEnumerable<ReconciliationQueueRow>> PollQueuedFailuresAsync(int maxAttempts, int batchSize) =>
        sql.QueryAsync<ReconciliationQueueRow>(
            $"""
            SELECT "Id", "TypeName", "EntityKey", "Attempts", "EventType", "Payload"
            FROM "{tableName}"
            WHERE "Attempts" < @MaxAttempts
            ORDER BY "EnqueuedAt"
            LIMIT @BatchSize
            """,
            new { MaxAttempts = maxAttempts, BatchSize = batchSize });

    public Task<int> CountExhaustedAsync(int maxAttempts) =>
        sql.QuerySingleOrDefaultAsync<int>(
            $"""SELECT COUNT(*) FROM "{tableName}" WHERE "Attempts" >= @MaxAttempts""",
            new { MaxAttempts = maxAttempts });

    public Task<int> CountPendingAsync() =>
        sql.QuerySingleOrDefaultAsync<int>($"""SELECT COUNT(*) FROM "{tableName}" """);

    public Task RecordFailureAsync(Guid id, int attempts, string lastError) =>
        sql.ExecuteAsync(
            $"""
            UPDATE "{tableName}"
            SET "Attempts" = @Attempts, "LastError" = @LastError, "LastAttemptAt" = @Now
            WHERE "Id" = @Id
            """,
            new { Attempts = attempts, LastError = lastError, Now = DateTimeOffset.UtcNow, Id = id });

    public Task DeleteRowAsync(Guid id) =>
        sql.ExecuteAsync($"""DELETE FROM "{tableName}" WHERE "Id" = @Id""", new { Id = id });
}
