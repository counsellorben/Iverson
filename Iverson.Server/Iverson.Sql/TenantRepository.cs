namespace Iverson.Sql;

public sealed class TenantRepository(
    string tableName,
    IRecordStoreQueryExecutor sql) : ITenantRepository
{
    public Task InsertAsync(string id, string displayName, string status) =>
        sql.ExecuteAsync(
            $"""
            INSERT INTO "{tableName}" ("Id", "DisplayName", "Status", "CreatedAt")
            VALUES (@Id, @DisplayName, @Status, @CreatedAt)
            """,
            new { Id = id, DisplayName = displayName, Status = status, CreatedAt = DateTimeOffset.UtcNow });

    public Task SeedIfMissingAsync(string id, string displayName, string status) =>
        sql.ExecuteAsync(
            $"""
            INSERT INTO "{tableName}" ("Id", "DisplayName", "Status", "CreatedAt")
            VALUES (@Id, @DisplayName, @Status, @CreatedAt)
            ON CONFLICT ("Id") DO NOTHING
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
            $"""UPDATE "{tableName}" SET "Status" = @Status WHERE "Id" = @Id""",
            new { Id = id, Status = status });

    public Task DeleteAsync(string id) =>
        sql.ExecuteAsync(
            $"""DELETE FROM "{tableName}" WHERE "Id" = @Id""",
            new { Id = id });
}
