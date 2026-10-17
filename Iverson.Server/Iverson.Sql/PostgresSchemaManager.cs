using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Iverson.Sql;

public sealed class PostgresSchemaManager(
    string connectionString,
    ILogger<PostgresSchemaManager> logger) : IRecordStoreSchemaManager
{
    /// <summary>
    /// The api's own login (CSR round 12 §6.1). A constant, like every other role name here. The
    /// chart's CNPG managed role and compose's worker both create exactly this name.
    /// </summary>
    public const string ApiLoginName = "iverson_api";

    private NpgsqlConnection CreateConnection() => new(connectionString);

    public async Task ApplySchemaAsync(TableSchema schema, SchemaDriftPolicy driftPolicy = SchemaDriftPolicy.Warn)
    {
        // Every name is quoted before any connection is opened, so a name the helper refuses never
        // reaches the advisory lock or any DDL. The column DDL below is non-transactional: quoting
        // a later column mid-loop would leave the earlier ones already added.
        var table     = PostgresIdentifier.Quote(schema.TableName);
        var keyColumn = PostgresIdentifier.Quote(schema.KeyColumn.Name);
        var quotedColumns = schema.Columns
            .Select(c => (Column: c, Quoted: PostgresIdentifier.Quote(c.Name)))
            .ToList();
        var quotedTenantColumn = schema.TenantColumn is null ? null : PostgresIdentifier.Quote(schema.TenantColumn);
        var quotedPolicyName   = schema.TenantColumn is null
            ? null
            : PostgresIdentifier.Quote($"{schema.TableName}_tenant_isolation");

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
                var keySql   = $"{keyColumn} {schema.KeyColumn.SqlType} PRIMARY KEY";
                var colsSql  = quotedColumns.Select(q =>
                    $"{q.Quoted} {q.Column.SqlType}{(q.Column.IsNullable ? "" : " NOT NULL")}");

                var ddl = $"""
                    CREATE TABLE IF NOT EXISTS {table} (
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

                foreach (var (col, quotedName) in quotedColumns.Where(q => !existingColumns.Contains(q.Column.Name)))
                {
                    var alterSql = $"""
                        ALTER TABLE {table}
                        ADD COLUMN IF NOT EXISTS {quotedName} {col.SqlType}{(col.IsNullable ? "" : $" NOT NULL DEFAULT ('{GetDefaultForType(col.SqlType)}')")}
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
                    // orphan is read back from pg_attribute, not taken from the schema, so it is not
                    // passed through PostgresIdentifier.Quote.
                    await conn.ExecuteAsync(
                        $"ALTER TABLE {table} DROP COLUMN IF EXISTS \"{orphan}\"");
                }
            }

            foreach (var (col, quotedName) in quotedColumns
                .Where(q =>
                    q.Column.Name.EndsWith("Id", StringComparison.OrdinalIgnoreCase) ||
                    q.Column.Name.EndsWith("Ids", StringComparison.OrdinalIgnoreCase)))
            {
                var idxName = $"ix_{schema.TableName}_{col.Name}".ToLowerInvariant();
                await conn.ExecuteAsync($"""
                    CREATE INDEX IF NOT EXISTS {PostgresIdentifier.Quote(idxName)}
                    ON {table} ({quotedName})
                    """);
            }

            // Unconditional, unlike the iverson_runtime grant below: iverson_maintenance is the
            // role every deliberately-cross-tenant entity access runs under (EntityAccess
            // .CrossTenantMaintenance), and those callers reach tables with and without a tenant
            // column alike — a reconciliation replay of a type that declares no tenant field is
            // still a maintenance read. Granting only the tenant-scoped subset would turn those
            // into 42501 insufficient_privilege at runtime.
            await conn.ExecuteAsync($"""GRANT SELECT, INSERT, UPDATE, DELETE ON {table} TO iverson_maintenance""");

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
                            CREATE POLICY {quotedPolicyName} ON {table}
                            USING ({quotedTenantColumn} = current_setting('app.tenant_id', true))
                            """);
                    }
                    catch (PostgresException ex) when (ex.SqlState == "42710")
                    {
                        // Another replica created it concurrently between our check and this CREATE — fine.
                    }
                }

                await conn.ExecuteAsync($"""ALTER TABLE {table} ENABLE ROW LEVEL SECURITY""");

                // ENABLE alone leaves the table's OWNER exempt from its own policy, and the api
                // connects as the owner (`iverson`, charts/api/templates/deployment.yaml against
                // the CNPG `bootstrap.initdb.owner`). So without FORCE the policy was inert on
                // every statement that did not first SET ROLE away from the owner — RLS was not
                // the independent second layer the threat model claims. FORCE is idempotent, which
                // matters because Program.cs re-applies this DDL for every registered descriptor
                // on every startup.
                await conn.ExecuteAsync($"""ALTER TABLE {table} FORCE ROW LEVEL SECURITY""");

                await conn.ExecuteAsync($"""GRANT SELECT, INSERT, UPDATE, DELETE ON {table} TO iverson_runtime""");
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

        // Existence and BYPASSRLS are not enough: this connection must also be able to ENTER each
        // role. An operator who runs the CREATE ROLE half of the cutover but forgets
        // `GRANT ... TO iverson` produces a cluster where every check above passes — the role
        // exists, its attributes are right, and ApplySchemaAsync's GRANT to it succeeds, because a
        // table's owner may grant to a role it is not a member of. Startup then comes up green and
        // the failure lands at runtime instead: the first `SET LOCAL ROLE` throws 42501 and every
        // tenant-scoped read, every reconciliation replay, every re-render queue item and every
        // consumer tenant/owner re-derivation starts failing in production.
        await EnsureCanEnterRoleAsync(conn, "iverson_runtime");
        await EnsureCanEnterRoleAsync(conn, "iverson_maintenance");
    }

    public async Task EnsureCanEnterRuntimeRoleAsync()
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();

        await EnsureCanEnterRoleAsync(conn, "iverson_runtime");
    }

    public async Task EnsureApiLoginAsync(string? password, IReadOnlyList<string> grantStatements, TimeSpan roleWait)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();

        // On Helm the login is a CNPG managed role, which CNPG creates asynchronously (as
        // job-revoke-cross-db.yaml's wait for authentik shows), so a worker that starts first waits
        // for it instead of crash-looping. Compose has no CNPG: there the worker is the superuser
        // and creates the login itself, from the password it is given.
        if (string.IsNullOrEmpty(password))
            await WaitForRoleAsync(conn, ApiLoginName, roleWait);

        await using var tx = await conn.BeginTransactionAsync();

        // Serialises worker replicas. Concurrent GRANTs on one table fail with XX000 "tuple
        // concurrently updated" (see ApplySchemaAsync), and two replicas could both try CREATE ROLE.
        // Transaction-scoped, so the lock is never left on a pooled connection.
        await conn.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtext('iverson_api_login')::bigint)", null, tx);

        try
        {
            if (!await RoleExistsAsync(conn, ApiLoginName, tx))
            {
                // Reached only with a password, since the wait above throws otherwise. CREATE ROLE
                // takes no bind parameters, so format's %L quotes the password as a literal.
                var createRole = await conn.QuerySingleAsync<string>(
                    $"SELECT format('CREATE ROLE {ApiLoginName} LOGIN PASSWORD %L', @Password::text)",
                    new { Password = password }, tx);
                await conn.ExecuteAsync(createRole, null, tx);
            }

            var isRuntimeMember = await conn.QuerySingleAsync<bool>(
                $"SELECT pg_has_role('{ApiLoginName}', 'iverson_runtime', 'MEMBER')", null, tx);
            if (!isRuntimeMember)
                await conn.ExecuteAsync($"GRANT iverson_runtime TO {ApiLoginName}", null, tx);

            foreach (var statement in grantStatements)
                await conn.ExecuteAsync(statement, null, tx);
        }
        catch (PostgresException ex) when (ex.SqlState == "42501")
        {
            throw new InvalidOperationException(
                $"This connection ({conn.UserName}) cannot set up the api's login {ApiLoginName}: "
                + $"{ex.MessageText}. On Helm the login and its iverson_runtime membership come from the "
                + "CNPG managed role in charts/postgres/templates/cluster.yaml, and the worker, as the "
                + "database owner, only grants table privileges: check the Cluster's status. On compose "
                + "the worker must connect as the superuser.", ex);
        }

        await tx.CommitAsync();
    }

    private static async Task WaitForRoleAsync(NpgsqlConnection conn, string roleName, TimeSpan wait)
    {
        var deadline = DateTimeOffset.UtcNow + wait;
        while (!await RoleExistsAsync(conn, roleName))
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new InvalidOperationException(
                    $"Login {roleName} does not exist, and this worker has no Postgres:ApiLoginPassword "
                    + "(env Postgres__ApiLoginPassword) to create it with. On Helm it is a CNPG managed role "
                    + "(charts/postgres/templates/cluster.yaml): check the Cluster's status for a role "
                    + "reconciliation error. On compose, set Postgres__ApiLoginPassword on the worker.");
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    private static Task<bool> RoleExistsAsync(NpgsqlConnection conn, string roleName, NpgsqlTransaction? tx = null) =>
        conn.QuerySingleAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @Role)", new { Role = roleName }, tx);

    /// <summary>
    /// Asserts this connection can enter <paramref name="roleName"/> by issuing the very statement
    /// the runtime issues, inside a transaction that is then rolled back.
    /// <para>
    /// A functional probe rather than a <c>pg_has_role</c> catalogue lookup: <c>SET ROLE</c> is
    /// gated by the role's SET option in PostgreSQL 16+ but by plain membership before it, so no
    /// single privilege name is both version-portable and exact — whereas the statement itself is
    /// exact by construction on every version. A superuser passes unconditionally, which is what
    /// docker-compose and the Testcontainers fixtures are.
    /// </para>
    /// </summary>
    private static async Task EnsureCanEnterRoleAsync(NpgsqlConnection conn, string roleName)
    {
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            // roleName is a compile-time constant from EnsureRolesAsync, never caller input.
            await conn.ExecuteAsync($"SET LOCAL ROLE {roleName}", null, tx);
        }
        catch (PostgresException ex) when (ex.SqlState == "42501")
        {
            throw new InvalidOperationException(
                $"Role {roleName} exists but this connection is not a member of it, so no statement "
                + "can enter it. Every access that names this role would fail at runtime with 42501. "
                + $"Run `GRANT {roleName} TO {conn.UserName};` as a superuser "
                + "(`kubectl cnpg psql <release>-postgres -- -d iverson`), then restart — the grant "
                + $"target is this connection's own role ({conn.UserName}), not CURRENT_USER as "
                + "evaluated inside that psql session, which connects as the cluster superuser. See "
                + "docs/runbooks/rls-force-maintenance-role-cutover.md.", ex);
        }

        // No commit: SET LOCAL unwinds with the transaction, so the pooled connection is handed
        // back on its original role either way.
        await tx.RollbackAsync();
    }

    private static async Task EnsureRoleAsync(NpgsqlConnection conn, string roleName, string createSql)
    {
        if (await RoleExistsAsync(conn, roleName)) return;

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
