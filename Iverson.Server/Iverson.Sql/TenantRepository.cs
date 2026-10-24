namespace Iverson.Sql;

public sealed class TenantRepository(
    string tableName,
    IRecordStoreQueryExecutor sql) : ITenantRepository
{
    // CSR round 12 §2.3: every write below notifies the api replicas in its own statement
    // (AuthInvalidation), once per row it writes. A seed that inserts nothing notifies nothing.
    public Task InsertAsync(string id, string displayName, string status) =>
        sql.ExecuteAsync(
            $"""
            INSERT INTO "{tableName}" ("Id", "DisplayName", "Status", "CreatedAt")
            VALUES (@Id, @DisplayName, @Status, @CreatedAt)
            {AuthInvalidation.ReturningTenantStatusNotification}
            """,
            new { Id = id, DisplayName = displayName, Status = status, CreatedAt = DateTimeOffset.UtcNow });

    public Task SeedIfMissingAsync(string id, string displayName, string status) =>
        sql.ExecuteAsync(
            $"""
            INSERT INTO "{tableName}" ("Id", "DisplayName", "Status", "CreatedAt")
            VALUES (@Id, @DisplayName, @Status, @CreatedAt)
            ON CONFLICT ("Id") DO NOTHING
            {AuthInvalidation.ReturningTenantStatusNotification}
            """,
            new { Id = id, DisplayName = displayName, Status = status, CreatedAt = DateTimeOffset.UtcNow });

    public Task<TenantRow?> GetAsync(string id) =>
        sql.QuerySingleOrDefaultAsync<TenantRow>(
            $"""SELECT "Id", "DisplayName", "Status", "CreatedAt" FROM "{tableName}" WHERE "Id" = @Id""",
            new { Id = id });

    public Task<IEnumerable<TenantRow>> ListAsync() =>
        sql.QueryAsync<TenantRow>(
            $"""SELECT "Id", "DisplayName", "Status", "CreatedAt" FROM "{tableName}" ORDER BY "CreatedAt" """);

    public Task UpdateStatusAsync(string id, string status) =>
        sql.ExecuteAsync(
            $"""UPDATE "{tableName}" SET "Status" = @Status WHERE "Id" = @Id {AuthInvalidation.ReturningTenantStatusNotification}""",
            new { Id = id, Status = status });

    // The RETURNING clause leaves the affected-row count the UPDATE's own.
    public async Task<bool> UpdateStatusUnlessDeletedAsync(string id, string status) =>
        await sql.ExecuteAsync(
            $"""UPDATE "{tableName}" SET "Status" = @Status WHERE "Id" = @Id AND "Status" <> 'deleted' {AuthInvalidation.ReturningTenantStatusNotification}""",
            new { Id = id, Status = status }) > 0;

    public Task DeleteAsync(string id) =>
        sql.ExecuteAsync(
            $"""DELETE FROM "{tableName}" WHERE "Id" = @Id {AuthInvalidation.ReturningTenantStatusNotification}""",
            new { Id = id });
}
