namespace Iverson.Sql;

/// <summary>
/// Startup DDL for a plumbing table that the api and the worker both run on boot.
/// <c>CREATE TABLE IF NOT EXISTS</c> and <c>CREATE INDEX IF NOT EXISTS</c> are not atomic: the
/// existence check and the create are separate steps, so two processes starting against a fresh
/// database can collide on the catalogue and the loser gets SQLSTATE 23505
/// (<c>pg_type_typname_nsp_index</c> and similar) instead of "already exists".
/// </summary>
internal static class StartupDdl
{
    /// <summary>
    /// <paramref name="ddl"/> preceded by a transaction-scoped advisory lock keyed on
    /// <paramref name="lockName"/> (the same <c>hashtext</c> key scheme <see cref="PostgresSchemaManager"/>
    /// uses for its per-table lock). The statements travel as one command, which Postgres runs as
    /// one implicit transaction, so the lock is held across the whole DDL and released when it
    /// ends: no unlock to forget, and nothing left on a pooled connection. <paramref name="lockName"/>
    /// must be a compile-time constant, never caller input.
    /// </summary>
    public static string Serialised(string lockName, string ddl) =>
        $"SELECT pg_advisory_xact_lock(hashtext('{lockName}')::bigint);\n{ddl}";
}
