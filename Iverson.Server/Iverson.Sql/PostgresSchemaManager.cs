using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Iverson.Sql;

public sealed class PostgresSchemaManager(
    string connectionString,
    ILogger<PostgresSchemaManager> logger) : IRecordStoreSchemaManager
{
    private NpgsqlConnection CreateConnection() => new(connectionString);

    public async Task ApplySchemaAsync(TableSchema schema, SchemaDriftPolicy driftPolicy = SchemaDriftPolicy.Warn)
    {
        using var activity = Telemetry.Source.StartActivity("db.apply_schema", ActivityKind.Client);
        activity?.SetTag("db.system", "postgresql");
        activity?.SetTag("db.table", schema.TableName);

        await using var conn = CreateConnection();
        await conn.OpenAsync();

        // Serialise the whole apply against other processes doing the same table. Without this,
        // `iverson-api` and `iverson-worker` booting simultaneously both run Program.cs's
        // self-heal loop over every registered descriptor, and TWO distinct races fire — both
        // reproduced by removing this lock and running
        // PostgresIntegrationTests.ApplySchemaAsync_EightConcurrentApplies*:
        //
        //   * Cold table: CREATE TABLE IF NOT EXISTS is NOT atomic — the existence check and the
        //     create are separate steps, so concurrent creators collide on the catalogue with
        //     23505 "duplicate key value violates unique constraint pg_type_typname_nsp_index".
        //     Deterministic; fires on first contact.
        //   * Existing table: the ENABLE / FORCE ROW LEVEL SECURITY and GRANT statements below all
        //     rewrite the table's pg_class row, and Postgres answers the loser with XX000 "tuple
        //     concurrently updated". Intermittent — ~3 runs in 5 at 8 callers, so a single green
        //     run proves nothing about it.
        //
        // Neither is caught anywhere, so the process exits and only `restart: unless-stopped`
        // recovers it. That is every routine redeploy of the two-role deployment, not just an
        // upgrade.
        //
        // A SESSION-level lock (not pg_advisory_xact_lock) because these statements are
        // deliberately non-transactional — see the drift comment below. The unlock is in the
        // finally rather than left to connection close: Npgsql pools connections, and a pooled
        // connection handed back while still holding a session advisory lock keeps holding it.
        await conn.ExecuteAsync(
            "SELECT pg_advisory_lock(hashtext(@TableName)::bigint)", new { schema.TableName });

        try
        {
            var existingColumnRows = (await conn.QueryAsync<(string Name, string Type)>(
                """
                SELECT a.attname AS name, format_type(a.atttypid, a.atttypmod) AS type
                FROM pg_attribute a
                JOIN pg_class c ON c.oid = a.attrelid
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public' AND c.relname = @TableName
                  AND a.attnum > 0 AND NOT a.attisdropped
                """,
                new { schema.TableName })).ToList();

            var existingColumns = existingColumnRows
                .Select(c => c.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (existingColumns.Count == 0)
            {
                var keySql   = $"\"{schema.KeyColumn.Name}\" {schema.KeyColumn.SqlType} PRIMARY KEY";
                var colsSql  = schema.Columns.Select(c =>
                    $"\"{c.Name}\" {c.SqlType}{(c.IsNullable ? "" : " NOT NULL")}");

                var ddl = $"""
                    CREATE TABLE IF NOT EXISTS "{schema.TableName}" (
                        {keySql},
                        {string.Join(",\n    ", colsSql)}
                    )
                    """;

                logger.LogInformation("Creating table {Table}", schema.TableName);
                await conn.ExecuteAsync(ddl);
            }
            else
            {
                // Drift detection runs BEFORE any DDL. Under SchemaDriftPolicy.Throw the whole
                // registration is rejected, and these statements are NOT transactional — running
                // the ADD/DROP loops first would leave a mutated table (including DROPPED columns,
                // i.e. data loss) behind a registration that was never recorded.
                var actualTypeByName = existingColumnRows.ToDictionary(
                    c => c.Name, c => c.Type, StringComparer.OrdinalIgnoreCase);

                var checkedColumns = schema.Columns
                    .Append(schema.KeyColumn)
                    .Where(c => existingColumns.Contains(c.Name));

                foreach (var col in checkedColumns)
                {
                    var actual = actualTypeByName[col.Name];
                    var expected = col.SqlType;

                    if (!string.Equals(NormalizePgType(actual), NormalizePgType(expected), StringComparison.Ordinal))
                    {
                        if (driftPolicy == SchemaDriftPolicy.Throw)
                        {
                            throw new SchemaDriftException(schema.TableName, col.Name, actual, expected);
                        }

                        logger.LogWarning(
                            "Column {Column} on table {Table} has type '{Actual}' but the registered schema expects '{Expected}'. "
                            + "Migrate the column by hand, then retry registration.",
                            col.Name, schema.TableName, actual, expected);
                    }
                }

                foreach (var col in schema.Columns.Where(c => !existingColumns.Contains(c.Name)))
                {
                    var alterSql = $"""
                        ALTER TABLE "{schema.TableName}"
                        ADD COLUMN IF NOT EXISTS "{col.Name}" {col.SqlType}{(col.IsNullable ? "" : $" NOT NULL DEFAULT ('{GetDefaultForType(col.SqlType)}')")}
                        """;

                    logger.LogInformation("Adding column {Column} to {Table}", col.Name, schema.TableName);
                    await conn.ExecuteAsync(alterSql);
                }

                var schemaColumnNames = schema.Columns
                    .Select(c => c.Name)
                    .Append(schema.KeyColumn.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var orphan in existingColumns.Where(c => !schemaColumnNames.Contains(c)))
                {
                    if (logger.IsEnabled(LogLevel.Information))
                        logger.LogInformation("Dropping removed column {Column} from {Table}", orphan, schema.TableName);
                    await conn.ExecuteAsync(
                        $"ALTER TABLE \"{schema.TableName}\" DROP COLUMN IF EXISTS \"{orphan}\"");
                }
            }

            foreach (var col in schema
                .Columns
                .Where(c =>
                    c.Name.EndsWith("Id", StringComparison.OrdinalIgnoreCase) ||
                    c.Name.EndsWith("Ids", StringComparison.OrdinalIgnoreCase)))
            {
                var idxName = $"ix_{schema.TableName}_{col.Name}".ToLowerInvariant();
                await conn.ExecuteAsync($"""
                    CREATE INDEX IF NOT EXISTS "{idxName}"
                    ON "{schema.TableName}" ("{col.Name}")
                    """);
            }

            // Unconditional, unlike the iverson_runtime grant below: iverson_maintenance is the
            // role every deliberately-cross-tenant entity access runs under (EntityAccess
            // .CrossTenantMaintenance), and those callers reach tables with and without a tenant
            // column alike — a reconciliation replay of a type that declares no tenant field is
            // still a maintenance read. Granting only the tenant-scoped subset would turn those
            // into 42501 insufficient_privilege at runtime.
            await conn.ExecuteAsync($"""GRANT SELECT, INSERT, UPDATE, DELETE ON "{schema.TableName}" TO iverson_maintenance""");

            if (schema.TenantColumn is not null)
            {
                var policyName = $"{schema.TableName}_tenant_isolation";
                var policyExists = await conn.QuerySingleOrDefaultAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM pg_policies WHERE tablename = @Table AND policyname = @Policy)",
                    new { Table = schema.TableName, Policy = policyName });

                if (!policyExists)
                {
                    try
                    {
                        await conn.ExecuteAsync($"""
                            CREATE POLICY "{policyName}" ON "{schema.TableName}"
                            USING ("{schema.TenantColumn}" = current_setting('app.tenant_id', true))
                            """);
                    }
                    catch (PostgresException ex) when (ex.SqlState == "42710")
                    {
                        // Another replica created it concurrently between our check and this CREATE — fine.
                    }
                }

                await conn.ExecuteAsync($"""ALTER TABLE "{schema.TableName}" ENABLE ROW LEVEL SECURITY""");

                // ENABLE alone leaves the table's OWNER exempt from its own policy, and the api
                // connects as the owner (`iverson`, charts/api/templates/deployment.yaml against
                // the CNPG `bootstrap.initdb.owner`). So without FORCE the policy was inert on
                // every statement that did not first SET ROLE away from the owner — RLS was not
                // the independent second layer the threat model claims. FORCE is idempotent, which
                // matters because Program.cs re-applies this DDL for every registered descriptor
                // on every startup.
                await conn.ExecuteAsync($"""ALTER TABLE "{schema.TableName}" FORCE ROW LEVEL SECURITY""");

                await conn.ExecuteAsync($"""GRANT SELECT, INSERT, UPDATE, DELETE ON "{schema.TableName}" TO iverson_runtime""");
            }

            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.RecordException(ex);
            throw;
        }
        finally
        {
            await conn.ExecuteAsync(
                "SELECT pg_advisory_unlock(hashtext(@TableName)::bigint)", new { schema.TableName });
        }
    }

    public async Task EnsureRolesAsync()
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();

        await EnsureRoleAsync(conn, "iverson_runtime", "CREATE ROLE iverson_runtime NOLOGIN");

        // The counterpart to FORCE ROW LEVEL SECURITY. Once the policy binds the owner too, the
        // genuinely cross-tenant callers (reconciliation replays, and the consumer paths that
        // re-derive an entity's authoritative tenant/owner value before any tenant is known) need
        // somewhere to stand that is not "happens to be the owner" — this is it, and choosing it
        // is visible in source as EntityAccess.CrossTenantMaintenance.
        await EnsureRoleAsync(conn, "iverson_maintenance", "CREATE ROLE iverson_maintenance NOLOGIN BYPASSRLS");

        // A pre-existing iverson_maintenance without BYPASSRLS would not error — it would quietly
        // return zero rows to every maintenance read, i.e. reconciliation silently reprojecting
        // nothing. Verify rather than assume, and fail startup loudly if it cannot be repaired.
        var bypassesRls = await conn.QuerySingleOrDefaultAsync<bool>(
            "SELECT rolbypassrls FROM pg_roles WHERE rolname = 'iverson_maintenance'");
        if (!bypassesRls)
        {
            try
            {
                await conn.ExecuteAsync("ALTER ROLE iverson_maintenance BYPASSRLS");
            }
            catch (PostgresException ex) when (ex.SqlState == "42501")
            {
                throw new InvalidOperationException(
                    "Role iverson_maintenance exists without BYPASSRLS and this connection lacks the "
                    + "privilege to add it. Every cross-tenant maintenance read would return zero rows. "
                    + "Run `ALTER ROLE iverson_maintenance BYPASSRLS;` as a superuser "
                    + "(`kubectl cnpg psql <release>-postgres -- -d iverson`) and restart.", ex);
            }
        }
    }

    private static async Task EnsureRoleAsync(NpgsqlConnection conn, string roleName, string createSql)
    {
        var exists = await conn.QuerySingleOrDefaultAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @Role)", new { Role = roleName });
        if (exists) return;

        try
        {
            await conn.ExecuteAsync(createSql);
        }
        catch (PostgresException ex) when (ex.SqlState == "42710")
        {
            // Another replica created it concurrently between our check and this CREATE
            // (this deployment runs multiple API replicas) — fine, it exists now either way.
        }
        catch (PostgresException ex) when (ex.SqlState == "42501")
        {
            // kubernetes: enableSuperuserAccess is false, so the app user can neither CREATE ROLE
            // nor (for maintenance) grant BYPASSRLS. The cluster's postInitApplicationSQL creates
            // both roles at initdb, but that hook does not re-run on an already-initialised
            // cluster — name the exact remedy rather than surfacing a bare 42501.
            throw new InvalidOperationException(
                $"Role {roleName} does not exist and this connection cannot create it. On an "
                + $"already-initialised cluster run `{createSql};` and `GRANT {roleName} TO iverson;` "
                + "as a superuser (`kubectl cnpg psql <release>-postgres -- -d iverson`), then restart.", ex);
        }
    }

    private static string NormalizePgType(string sqlType) => sqlType.Trim().ToLowerInvariant() switch
    {
        "timestamptz"   => "timestamp with time zone",
        "timestamptz[]" => "timestamp with time zone[]",
        var t           => t
    };

    private static string GetDefaultForType(string sqlType) => sqlType.ToUpperInvariant() switch
    {
        var t when t.EndsWith("[]")          => "{}",
        var t when t.StartsWith("INT")       => "0",
        var t when t.StartsWith("FLOAT")     => "0",
        var t when t.StartsWith("REAL")      => "0",
        var t when t.StartsWith("DOUBLE")    => "0",
        var t when t.StartsWith("BOOL")      => "false",
        var t when t.StartsWith("UUID")      => "00000000-0000-0000-0000-000000000000",
        var t when t.StartsWith("TIMESTAMP") => "1970-01-01 00:00:00+00",
        _                                    => ""
    };
}
