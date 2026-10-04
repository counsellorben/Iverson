namespace Iverson.Sql;

/// <summary>
/// CSR round-10 #11: one row per user whose tokens must stop working — a removed user, or a
/// demoted tenant admin — keyed by the token's <c>sub</c>. A token is refused when its
/// <c>iat</c> is at or before <c>revoked_at</c>, so a fresh login afterwards is accepted. Rows
/// are never pruned: the table holds one row per removed or demoted user, and pruning would
/// mean depending on token lifetimes.
/// </summary>
public sealed class TokenRevocationRepository(IRecordStoreQueryExecutor sql) : ITokenRevocationRepository
{
    public Task EnsureTableAsync() =>
        sql.ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS iverson_token_revocations (
                sub        TEXT PRIMARY KEY,
                revoked_at TIMESTAMPTZ NOT NULL
            )
            """);

    public Task RevokeAsync(string sub) =>
        sql.ExecuteAsync(
            """
            INSERT INTO iverson_token_revocations (sub, revoked_at)
            VALUES (@Sub, now())
            ON CONFLICT (sub) DO UPDATE
                SET revoked_at = EXCLUDED.revoked_at
            """,
            new { Sub = sub });

    // DateTime, not DateTimeOffset, in the query shape: Npgsql materializes timestamptz as a UTC
    // DateTime, and Dapper will not convert it (see TenantRow's note in IRecordStoreRoles.cs).
    public async Task<IEnumerable<(string Sub, DateTimeOffset RevokedAt)>> ListAsync()
    {
        var rows = await sql.QueryAsync<(string sub, DateTime revoked_at)>(
            "SELECT sub, revoked_at FROM iverson_token_revocations");
        return rows.Select(r => (r.sub, new DateTimeOffset(r.revoked_at, TimeSpan.Zero)));
    }
}
