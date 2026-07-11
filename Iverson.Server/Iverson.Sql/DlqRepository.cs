namespace Iverson.Sql;

public sealed class DlqRepository(
    string tableName,
    IRecordStoreQueryExecutor sql) : IDlqRepository
{
    public Task InsertAsync(DlqMessage message) =>
        sql.ExecuteAsync(
            $"""
            INSERT INTO "{tableName}"
                ("Id", "SourceTopic", "ConsumerGroup", "MessageKey", "MessageValue",
                 "ExceptionType", "ExceptionMessage", "Attempts", "FailedAt", "Replayed")
            VALUES
                (@Id, @SourceTopic, @ConsumerGroup, @MessageKey, @MessageValue,
                 @ExceptionType, @ExceptionMessage, @Attempts, @FailedAt, false)
            """,
            new
            {
                Id = Guid.CreateVersion7(),
                message.SourceTopic,
                message.ConsumerGroup,
                message.MessageKey,
                message.MessageValue,
                message.ExceptionType,
                message.ExceptionMessage,
                message.Attempts,
                message.FailedAt
            });

    public Task<IEnumerable<DlqRow>> ListUnreplayedAsync(int limit) =>
        sql.QueryAsync<DlqRow>(
            $"""
            SELECT "Id", "SourceTopic", "ConsumerGroup", "MessageKey", "ExceptionType",
                   "ExceptionMessage", "Attempts", "FailedAt", "Replayed"
            FROM "{tableName}"
            WHERE "Replayed" = false
            ORDER BY "FailedAt" DESC
            LIMIT @Limit
            """,
            new { Limit = limit });

    public Task<DlqReplayRow?> GetUnreplayedByIdAsync(Guid id) =>
        sql.QuerySingleOrDefaultAsync<DlqReplayRow>(
            $"""
            SELECT "SourceTopic", "MessageKey", "MessageValue"
            FROM "{tableName}"
            WHERE "Id" = @Id AND "Replayed" = false
            """,
            new { Id = id });

    public Task MarkReplayedAsync(Guid id) =>
        sql.ExecuteAsync(
            $"""UPDATE "{tableName}" SET "Replayed" = true WHERE "Id" = @Id""",
            new { Id = id });

    public Task<int> CountUnreplayedAsync() =>
        sql.QuerySingleOrDefaultAsync<int>(
            $"""SELECT COUNT(*) FROM "{tableName}" WHERE "Replayed" = false""");
}
