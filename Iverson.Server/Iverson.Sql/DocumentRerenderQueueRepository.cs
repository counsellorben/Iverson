namespace Iverson.Sql;

/// <summary>
/// Durable queue for re-rendering a type's document template when a related entity changes.
/// Two row shapes share one table: a per-entity row (<c>EntityKey</c> non-null) means "re-render
/// this one entity"; a type-level row (<c>EntityKey</c> null, <c>Cursor</c> used) means "re-render
/// every entity of this type, resuming from <c>Cursor</c>" — how a backfill is represented when the
/// key set is not yet known. The two partial unique indexes below enforce collapse for each shape;
/// their predicates are deliberately disjoint and jointly total (<c>EntityKey IS NOT NULL</c> vs.
/// <c>EntityKey IS NULL</c>), so every row falls under exactly one of them.
/// </summary>
public sealed class DocumentRerenderQueueRepository(IRecordStoreQueryExecutor sql) : IDocumentRerenderQueueRepository
{
    public const string TableName = "DocumentRerenderQueue";

    public Task EnsureTableAsync() =>
        sql.ExecuteAsync(
            $"""
            CREATE TABLE IF NOT EXISTS "{TableName}" (
                "Id"            uuid PRIMARY KEY,
                "TenantId"      TEXT,
                "TypeName"      TEXT NOT NULL,
                "EntityKey"     TEXT,
                "Cursor"        TEXT,
                "EnqueuedAt"    TIMESTAMPTZ NOT NULL,
                "Attempts"      INTEGER NOT NULL,
                "LastError"     TEXT,
                "LastAttemptAt" TIMESTAMPTZ
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_document_rerender_queue_entity
                ON "{TableName}" ("TenantId", "TypeName", "EntityKey")
                WHERE "EntityKey" IS NOT NULL;

            CREATE UNIQUE INDEX IF NOT EXISTS ux_document_rerender_queue_type
                ON "{TableName}" ("TypeName")
                WHERE "EntityKey" IS NULL;
            """);

    public Task EnqueueEntityAsync(string? tenantId, string typeName, string entityKey) =>
        sql.ExecuteAsync(
            $"""
            INSERT INTO "{TableName}" ("Id", "TenantId", "TypeName", "EntityKey", "EnqueuedAt", "Attempts")
            VALUES (@Id, @TenantId, @TypeName, @EntityKey, @EnqueuedAt, 0)
            ON CONFLICT DO NOTHING
            """,
            new
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                TypeName = typeName,
                EntityKey = entityKey,
                EnqueuedAt = DateTimeOffset.UtcNow
            });

    // A new template invalidation must restart the scan from the beginning, even if a prior
    // backfill for this type is already mid-expansion (Cursor advanced): otherwise every entity
    // before the old cursor is skipped and re-rendered against the STALE template forever, since
    // the rendered document has no stored copy to later correct it against.
    public Task EnqueueTypeAsync(string typeName) =>
        sql.ExecuteAsync(
            $"""
            INSERT INTO "{TableName}" ("Id", "TypeName", "EntityKey", "Cursor", "EnqueuedAt", "Attempts")
            VALUES (@Id, @TypeName, null, null, @EnqueuedAt, 0)
            ON CONFLICT ("TypeName") WHERE "EntityKey" IS NULL
            DO UPDATE SET "Cursor" = NULL, "EnqueuedAt" = @EnqueuedAt, "Attempts" = 0, "LastError" = NULL
            """,
            new
            {
                Id = Guid.NewGuid(),
                TypeName = typeName,
                EnqueuedAt = DateTimeOffset.UtcNow
            });

    public Task<IEnumerable<DocumentRerenderQueueRow>> PollAsync(int maxAttempts, int batchSize) =>
        sql.QueryAsync<DocumentRerenderQueueRow>(
            $"""
            SELECT "Id", "TenantId", "TypeName", "EntityKey", "Cursor", "Attempts"
            FROM "{TableName}"
            WHERE "Attempts" < @MaxAttempts
            ORDER BY "EnqueuedAt"
            LIMIT @BatchSize
            """,
            new { MaxAttempts = maxAttempts, BatchSize = batchSize });

    public Task AdvanceCursorAsync(Guid id, string cursor) =>
        sql.ExecuteAsync(
            $"""UPDATE "{TableName}" SET "Cursor" = @Cursor WHERE "Id" = @Id""",
            new { Cursor = cursor, Id = id });

    public Task RecordFailureAsync(Guid id, int attempts, string lastError) =>
        sql.ExecuteAsync(
            $"""
            UPDATE "{TableName}"
            SET "Attempts" = @Attempts, "LastError" = @LastError, "LastAttemptAt" = @Now
            WHERE "Id" = @Id
            """,
            new { Attempts = attempts, LastError = lastError, Now = DateTimeOffset.UtcNow, Id = id });

    public Task DeleteRowAsync(Guid id) =>
        sql.ExecuteAsync($"""DELETE FROM "{TableName}" WHERE "Id" = @Id""", new { Id = id });

    public Task<int> CountPendingAsync() =>
        sql.QuerySingleOrDefaultAsync<int>($"""SELECT COUNT(*) FROM "{TableName}" """);

    public Task<int> CountExhaustedAsync(int maxAttempts) =>
        sql.QuerySingleOrDefaultAsync<int>(
            $"""SELECT COUNT(*) FROM "{TableName}" WHERE "Attempts" >= @MaxAttempts""",
            new { MaxAttempts = maxAttempts });
}
