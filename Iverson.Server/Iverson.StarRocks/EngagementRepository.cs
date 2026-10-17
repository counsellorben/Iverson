using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Dapper;
using Iverson.Client.Contracts;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Polly;
using Polly.CircuitBreaker;

namespace Iverson.StarRocks;

public sealed class EngagementRepository(
    string connectionString,
    ILogger<EngagementRepository> logger,
    EngagementResilienceOptions? resilienceOptions = null,
    EngagementQueryLimitOptions? queryLimitOptions = null,
    string? provisionerConnectionString = null,
    string? tenantLoginKey = null,
    IProvisionedTenantDirectory? provisionedTenants = null)
    : IEngagementStoreQueryExecutor, IEngagementStoreEntityStore, IEngagementStoreSearchService
{
    private readonly EngagementResilienceOptions _resilience = resilienceOptions ?? EngagementResilienceOptions.Default;
    private readonly EngagementQueryLimitOptions _queryLimits = queryLimitOptions ?? EngagementQueryLimitOptions.Default;

    private readonly StarRocksReadinessGate _readinessGate = new(
        ct => CheckBackendAliveAsync(connectionString, ct),
        (resilienceOptions ?? EngagementResilienceOptions.Default).BackendReadyTimeout);

    private readonly ResiliencePipeline _pipeline =
        StarRocksResiliencePipelineFactory.Build(
            (resilienceOptions ?? EngagementResilienceOptions.Default).CircuitBreaker, logger);

    private MySqlConnection CreateConnection() => new(connectionString);

    // CSR round 11 §3: tenant provisioning's own login, which only the worker is given. Used by
    // EnsureTenantProvisionedAsync and nothing else; every query runs on CreateConnection's login.
    private MySqlConnection CreateProvisionerConnection() => new(provisionerConnectionString);

    // ── Per-tenant logins (CSR round 12 §7.2) ───────────────────────────────────
    // Every tenant-scoped data connection logs in as the tenant's own StarRocks user, whose default role is
    // the tenant's role, so it reaches that tenant's database and nothing else. The base login
    // (connectionString, iverson_app) keeps only the readiness gate, the liveness probe and the authz-null
    // executor paths, and holds no data grant.

    /// <summary>The most connections one tenant's pool may hold in this process (spec §7.2).</summary>
    internal const uint TenantPoolSize = 10;

    /// <summary>Seconds an idle pooled tenant connection lives before it is reaped (spec §7.2), so an idle
    /// tenant's pool drains to zero connections.</summary>
    internal const uint TenantPoolIdleSeconds = 60;

    /// <summary>The per-session caps every tenant login carries: the pair job-create-user.yaml sets on
    /// iverson_app (spec §7.1). Keep the two equal.</summary>
    internal const string TenantSessionProperties =
        "'session.query_timeout' = '180', 'session.exec_mem_limit' = '4294967296'";

    private const string NoTenantLoginsMessage =
        "Tenant-scoped StarRocks access needs the tenant login key (StarRocks:TenantKey) and the " +
        "provisioned-tenant directory, and this repository was not given both.";

    /// <summary>
    /// One tenant's connection string: <paramref name="baseConnectionString"/> with the tenant's own user and
    /// derived password, no default database (every statement is qualified, and a default database the user
    /// cannot use fails the handshake), and the per-tenant pool bounds. MySqlConnector's pooled reset is off:
    /// StarRocks' reset puts system session variables back to their global defaults, which would drop the caps
    /// the login carries (spec Verified assumption 26). Deterministic, so every call for one tenant yields the
    /// identical string and therefore MySqlConnector's one pool for it. Another tenant gets another string and
    /// a pool of its own, so session state a pooled connection keeps across checkouts never reaches another
    /// tenant.
    /// </summary>
    internal static string TenantConnectionString(string baseConnectionString, string tenantId, string tenantLoginKey) =>
        new MySqlConnectionStringBuilder(baseConnectionString)
        {
            UserID                = TenantIdentifier.LoginName(tenantId),
            Password              = TenantIdentifier.LoginPassword(tenantId, tenantLoginKey),
            Database              = "",
            MaximumPoolSize       = TenantPoolSize,
            ConnectionIdleTimeout = TenantPoolIdleSeconds,
            ConnectionReset       = false,
        }.ToString();

    private string TenantConnectionStringFor(string tenantId) =>
        TenantConnectionString(connectionString, tenantId,
            tenantLoginKey ?? throw new InvalidOperationException(NoTenantLoginsMessage));

    // CSR round 12 §7.3: a tenant the worker has not recorded as provisioned has no login yet, so the readers
    // answer it as empty, and DeleteAsync, which has nothing to remove, returns, all without connecting.
    private Task<bool> IsRecordedAsync(string tenantId) =>
        (provisionedTenants ?? throw new InvalidOperationException(NoTenantLoginsMessage))
            .IsProvisionedAsync(tenantId);

    // ── Resilience chokepoint ───────────────────────────────────────────────────
    // Every real StarRocks operation routes through here: first the one-time cold-start
    // gate (near-instant no-op after the first success), then the circuit-breaker+retry
    // pipeline for ongoing protection against a backend that later goes unhealthy.
    // `ct` cuts only this caller's wait at the gate short. It is not handed to EnsureReadyAsync: the gate's one
    // wait is shared, so a token passed there would cancel it for every concurrent caller.
    private async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken ct = default)
    {
        await _readinessGate.EnsureReadyAsync().WaitAsync(ct).ConfigureAwait(false);

        try
        {
            return await _pipeline
                .ExecuteAsync(async _ => await operation().ConfigureAwait(false))
                .ConfigureAwait(false);
        }
        catch (BrokenCircuitException ex)
        {
            throw new EngagementNotReadyException(
                "StarRocks is currently unavailable (circuit breaker open).", ex);
        }
        catch (MySqlException ex)
        {
            if (await RewrapIfBackendUnavailableAsync(
                    ex, probeCt => CheckBackendAliveAsync(connectionString, probeCt), LivenessCheckBound)
                .ConfigureAwait(false) is { } unavailable)
                throw unavailable;
            throw;
        }
    }

    /// <summary>How long the post-failure backend liveness check may take before it counts as dead.</summary>
    internal static readonly TimeSpan LivenessCheckBound = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Decides whether a StarRocks failure is really a backend outage (spec §5). MySqlConnector marks only five
    /// error codes transient, and the circuit breaker counts failures with that same predicate, so an outage that
    /// is not a refused connection (a frozen server, a BE down behind a live FE) neither classifies as transient
    /// nor opens the circuit. For a <see cref="MySqlException"/> that is neither <see cref="MySqlException.IsTransient"/>
    /// nor <see cref="IsExpectedMissingResourceError"/>, this runs <paramref name="checkBackendAlive"/> bounded to
    /// <paramref name="bound"/>; a false result, a throw, or a timeout returns an
    /// <see cref="EngagementNotReadyException"/> wrapping <paramref name="ex"/>, which the projection consumers'
    /// classifier treats as transient and the gRPC read paths map to <c>Unavailable</c>. Null means rethrow the
    /// original. Expected missing-resource errors are exempt so that a read of an unwritten type or an
    /// unprovisioned tenant never pays a liveness round-trip.
    /// </summary>
    internal static async Task<EngagementNotReadyException?> RewrapIfBackendUnavailableAsync(
        Exception ex, Func<CancellationToken, Task<bool>> checkBackendAlive, TimeSpan bound,
        TimeProvider? timeProvider = null)
    {
        if (ex is not MySqlException mex || mex.IsTransient || IsExpectedMissingResourceError(mex))
            return null;

        bool alive;
        using var cts = new CancellationTokenSource();
        try
        {
            // WaitAsync is the only timer: it bounds a check that ignores its token. The token is cancelled on
            // failure (below) so a cooperative check, or an orphaned one, stops instead of running to the driver's
            // timeout. A second timer of the same length on the CTS would race WaitAsync's and could lose.
            // timeProvider exists so a test can elapse the bound deterministically; production uses the system clock.
            alive = await checkBackendAlive(cts.Token)
                .WaitAsync(bound, timeProvider ?? TimeProvider.System)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The check threw or timed out: the backend cannot be shown alive.
            cts.Cancel();
            alive = false;
        }

        return alive ? null : new EngagementNotReadyException("StarRocks backend unavailable", ex);
    }

    /// <summary>
    /// Opens the provisioning connection (CSR round 11 §3). On a cluster's first upgrade to the split
    /// logins, the post-upgrade hook that creates <c>iverson_provisioner</c> runs after the upgraded worker
    /// is already serving, so StarRocks refuses the login as AccessDenied until it does. That refusal
    /// becomes an <see cref="EngagementNotReadyException"/>, which the projection consumers treat as
    /// transient: they halt and redeliver instead of dead-lettering. The readiness checker treats the
    /// same not-yet-created case the same way (<see cref="EngagementHealthChecker"/>). AccessDenied is
    /// wire-level ambiguous, so a wrong password stalls the consumer the same way (spec, Known issues).
    /// Every other failure propagates unchanged.
    /// </summary>
    internal static Task OpenProvisionerConnectionAsync(Func<Task> open) =>
        OpenOrSurfaceRefusalAsync(open, "StarRocks refused the provisioner login; it may not have been created yet.");

    /// <summary>
    /// Opens a tenant's own connection (CSR round 12 §7.3). Readers connect only for a tenant the worker has
    /// recorded, and the worker records it only after creating its login, so a refusal here is a real fault (a
    /// login removed by hand, or a tenant login key other than the one the login was created with), never an
    /// empty tenant. It surfaces as <see cref="EngagementNotReadyException"/>: the gRPC read paths answer that
    /// with <c>Unavailable</c> rather than <c>Unknown</c>, and the projection consumers redeliver instead of
    /// dead-lettering. Every other failure propagates unchanged.
    /// </summary>
    internal static Task OpenTenantConnectionAsync(Func<Task> open) =>
        OpenOrSurfaceRefusalAsync(open, "StarRocks refused the tenant's login.");

    private static async Task OpenOrSurfaceRefusalAsync(Func<Task> open, string refusal)
    {
        try
        {
            await open().ConfigureAwait(false);
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.AccessDenied)
        {
            throw new EngagementNotReadyException(refusal, ex);
        }
    }

    public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null)
    {
        StarRocksParameterGuard.EnsureAllPlaceholdersBound(sql, param);

        using var activity = Telemetry.Source.StartActivity("sr.query", ActivityKind.Client);
        activity?.SetTag("db.system", "starrocks");
        activity?.SetTag("db.statement", sql);

        try
        {
            var results = await RunAsync(async () =>
            {
                await using var conn = CreateConnection();
                return await conn.QueryAsync<T>(sql, param);
            });
            activity?.SetStatus(ActivityStatusCode.Ok);
            return results;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.RecordException(ex);
            throw;
        }
    }

    public async Task<int> ExecuteAsync(string sql, object? param = null)
    {
        StarRocksParameterGuard.EnsureAllPlaceholdersBound(sql, param);

        using var activity = Telemetry.Source.StartActivity("sr.execute", ActivityKind.Client);
        activity?.SetTag("db.system", "starrocks");
        activity?.SetTag("db.statement", sql);

        try
        {
            var rows = await RunAsync(async () =>
            {
                await using var conn = CreateConnection();
                return await conn.ExecuteAsync(sql, param);
            });
            activity?.SetTag("db.rows_affected", rows);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return rows;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.RecordException(ex);
            throw;
        }
    }

    // The one "no data yet" shape on the read path that is treated as fail-closed-empty rather
    // than propagated as an exception: an unwritten type within a provisioned tenant.
    // EnsureTenantProvisionedAsync creates exactly one table per call (on that type's own first
    // write), not every table a tenant will ever use — so a tenant that has written type A but
    // queries type B (never written) has a perfectly valid login, role and database, but the
    // specific table doesn't exist. Verified empirically against a live container: querying a
    // nonexistent table under a database the active role legitimately has grants on raises
    // MySqlException with ErrorCode 5502 (SqlState 42602 — StarRocks-specific, no named
    // MySqlErrorCode member) and a message containing "Unknown table". Deliberately distinct
    // from — and must never accidentally match — two adjacent error shapes that must keep
    // propagating as real failures: querying a wrong/nonexistent *database* name (a real bug,
    // e.g. a tenant-ID-to-database-name mapping defect) raises a different message, "Unknown
    // database" (ErrorCode 5501); and an actual permissions problem on a table that legitimately
    // exists raises yet another message, "Access denied ... privilege(s) on TABLE ..." (ErrorCode
    // 5203). Confirmed both do NOT contain "Unknown table" and so are correctly left unmatched.
    //
    // An unprovisioned tenant is no longer an error shape (CSR round 12 §7.3): every reader asks
    // IProvisionedTenantDirectory first and answers a tenant the worker has not recorded as empty,
    // without connecting. The role arm this predicate used to carry ("cannot find role" / "is not
    // granted to", ParseError 1064 from SET ROLE) went with SET ROLE itself, so a recorded tenant
    // whose login or role is missing now surfaces as the fault it is.
    //
    // Matched as a specific, verified StarRocks/MySqlConnector error shape (not a bare catch) so
    // the read paths can treat it the same as every other "no data" case in this repository
    // (invalid tenant ID, missing tenant claim, unrecorded tenant: all return empty), while any
    // other StarRocks failure still propagates as an exception. Deliberately scoped to the READ
    // path only — UpsertAsync/DeleteAsync must keep throwing, since UpsertAsync only ever runs
    // after EnsureTenantProvisionedAsync has created that exact type's table in the same code path
    // (EngagementStoreConsumer), so hitting it there would mean a real invariant violation.
    private static bool IsExpectedMissingResourceError(Exception ex) =>
        ex is MySqlException mex &&
        mex.ErrorCode == (MySqlErrorCode)5502 &&
        mex.Message.Contains("Unknown table", StringComparison.OrdinalIgnoreCase);

    // isExpectedException: lets a read-path caller (SearchAsync/AggregateAsync/GroupByAsync/
    // PipelineAsync) identify IsExpectedMissingResourceError as a non-exceptional, expected
    // outcome (a brand-new tenant's first-ever read, or a provisioned tenant's first-ever read of
    // a type it has never written) *before* this method's own Activity is stopped — the caller's
    // own catch block runs after `using var activity` above has already disposed (and therefore
    // exported) the Activity, so setting its status there is too late. Left null (the default)
    // for UpsertAsync/DeleteAsync, which must keep marking Error: either shape on the write path
    // is a real invariant violation, not an expected case, and must not be masked from
    // error-rate telemetry.
    private async Task<T> RunTenantScopedAsync<T>(
        string activityName, string tenantId, string sql, Func<MySqlConnection, Task<T>> operation,
        Func<Exception, bool>? isExpectedException = null)
    {
        // Defensive re-validation: every current caller already checks TenantIdentifier.IsValid
        // before calling in, so this never fires today. It exists purely so a future caller that
        // forgets that check fails loudly and immediately, rather than deriving a login for an
        // unvalidated tenantId.
        if (!TenantIdentifier.IsValid(tenantId))
            throw new ArgumentException($"Invalid tenant ID: '{tenantId}'", nameof(tenantId));

        var tenantConnectionString = TenantConnectionStringFor(tenantId);

        using var activity = Telemetry.Source.StartActivity(activityName, ActivityKind.Client);
        activity?.SetTag("db.system", "starrocks");
        activity?.SetTag("db.statement", sql);

        try
        {
            var result = await RunAsync(async () =>
            {
                // CSR round 12 §7.2: the tenant's own login, whose default role is already active.
                // Nothing here changes the connection's role or session state, and the pool is this
                // tenant's alone, so whatever a pooled connection keeps stays inside the tenant.
                await using var conn = new MySqlConnection(tenantConnectionString);
                await OpenTenantConnectionAsync(() => conn.OpenAsync());
                return await operation(conn);
            });
            activity?.SetStatus(ActivityStatusCode.Ok);
            return result;
        }
        catch (Exception ex)
        {
            if (isExpectedException?.Invoke(ex) == true)
            {
                // Still rethrown below so the caller's own catch-and-return-empty logic runs —
                // only the telemetry verdict changes here, not the control flow.
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            else
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.RecordException(ex);
            }
            throw;
        }
    }

    /// <summary>
    /// Streams a tenant-scoped query row by row (spec §3.2). Unlike <see cref="RunTenantScopedAsync{T}"/>, the
    /// resilience pipeline wraps only open and query start; a failure after that propagates. The connection, on
    /// the tenant's own login (CSR round 12 §7.2), is held until enumeration ends and then returned to that
    /// tenant's pool. <paramref name="ct"/> reaches the query start and every read, and is checked before each row.
    /// </summary>
    internal async IAsyncEnumerable<IDictionary<string, object?>> StreamTenantScopedAsync(
        string activityName, string tenantId, string sql, object param,
        Func<Exception, bool> isExpectedMissingResource, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!TenantIdentifier.IsValid(tenantId))
            throw new ArgumentException($"Invalid tenant ID: '{tenantId}'", nameof(tenantId));

        var tenantConnectionString = TenantConnectionStringFor(tenantId);

        using var activity = Telemetry.Source.StartActivity(activityName, ActivityKind.Client);
        activity?.SetTag("db.system", "starrocks");
        activity?.SetTag("db.statement", sql);

        MySqlConnection? conn = null;
        DbDataReader? reader = null;
        try
        {
            (conn, reader) = await RunAsync(async () =>
            {
                var c = new MySqlConnection(tenantConnectionString);
                try
                {
                    await OpenTenantConnectionAsync(() => c.OpenAsync(ct));
                    var r = await c.ExecuteReaderAsync(new CommandDefinition(sql, param, cancellationToken: ct));
                    return (c, r);
                }
                catch
                {
                    await ReleaseAsync(c, null);
                    throw;
                }
            }, ct);
        }
        catch (Exception ex) when (isExpectedMissingResource(ex))
        {
            activity?.SetStatus(ActivityStatusCode.Ok);   // a missing table: empty, as SearchAsync
        }
        catch (Exception ex)
        {
            MarkFailed(activity, ex);
            throw;
        }

        if (reader is null) yield break;

        try
        {
            while (await ReadRowAsync(reader, activity, ct) is { } row)
                yield return row;
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        finally
        {
            await ReleaseAsync(conn!, reader);
        }
    }

    /// <summary>
    /// The next row, or null at the end. A read failure marks <paramref name="activity"/> failed here, because the
    /// iterator above cannot catch around its <c>yield return</c>; a failure in the consumer is not the read's.
    /// </summary>
    private static async ValueTask<IDictionary<string, object?>?> ReadRowAsync(DbDataReader reader, Activity? activity, CancellationToken ct)
    {
        try
        {
            if (!await reader.ReadAsync(ct)) return null;
            ct.ThrowIfCancellationRequested();
            var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var value = reader.GetValue(i);
                row[reader.GetName(i)] = value is DBNull ? null : value;   // spec §3.2: SQL NULL is C# null
            }
            return row;
        }
        catch (Exception ex)
        {
            MarkFailed(activity, ex);
            throw;
        }
    }

    /// <summary>As <see cref="RunTenantScopedAsync{T}"/> marks an unexpected failure.</summary>
    private static void MarkFailed(Activity? activity, Exception ex)
    {
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity?.RecordException(ex);
    }

    private async Task ReleaseAsync(MySqlConnection conn, DbDataReader? reader)
    {
        try
        {
            if (reader is not null) await reader.DisposeAsync();
        }
        catch (Exception ex)
        {
            // Releasing must never replace the read's own outcome.
            logger.LogWarning(ex, "Releasing a tenant-scoped StarRocks reader failed (reader disposal)");
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    public async IAsyncEnumerable<IDictionary<string, object?>> MatchRowsAsync(
        EngagementQuerySchema schema,
        MatchRowsRequest request,
        IReadOnlyDictionary<string, AuthorizationConstraint> authz,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var tenantId = authz.GetValueOrDefault(schema.TypeName)?.TenantValue;
        if (tenantId is null || !TenantIdentifier.IsValid(tenantId))
            yield break;

        var (sql, param) = MatchRowsQueryBuilder.Build(schema, request, authz, TenantIdentifier.DatabaseName(tenantId));

        if (!await IsRecordedAsync(tenantId))
            yield break;

        await foreach (var row in StreamTenantScopedAsync("sr.match_rows", tenantId, sql, param, IsExpectedMissingResourceError, ct))
            yield return row;
    }

    public async Task UpsertAsync(EngagementTableSchema schema, string payloadJson, string tenantId)
    {
        using var activity = Telemetry.Source.StartActivity("sr.upsert", ActivityKind.Client);
        activity?.SetTag("db.system", "starrocks");
        activity?.SetTag("db.table", schema.TableName);

        if (!TenantIdentifier.IsValid(tenantId)) return;

        var entries = UpsertEntries(schema, payloadJson);

        if (entries.Count == 0) return;

        var colList   = string.Join(", ", entries.Select(e => $"`{e.Key}`"));
        var paramList = string.Join(", ", entries.Select((_, i) => $"@p{i}"));
        var qualifiedTable = TenantIdentifier.Qualify(TenantIdentifier.DatabaseName(tenantId), schema.TableName);

        // StarRocks Primary Key model treats INSERT of an existing key as a FULL-ROW REPLACE:
        // any column absent from the INSERT list is reset to its default/null. (unchanged rationale
        // from the pre-existing comment here — see git history.)
        var sql = $"INSERT INTO {qualifiedTable} ({colList}) VALUES ({paramList})";

        var param = new DynamicParameters();
        for (var i = 0; i < entries.Count; i++)
            param.Add($"p{i}", JsonElementToObject(entries[i].Value));

        StarRocksParameterGuard.EnsureAllPlaceholdersBound(sql, param);
        await RunTenantScopedAsync("sr.execute", tenantId, sql, conn => conn.ExecuteAsync(sql, param));
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    /// <summary>
    /// The payload entries <see cref="UpsertAsync"/> writes. An entry is written when its key is
    /// exactly a column name, compared ordinally, and its value is not a JSON object.
    /// <para>
    /// Ordinal, because StarRocks resolves column names case-insensitively. A re-cased key such as
    /// <c>OWNERID</c> would otherwise go into the <c>OwnerId</c> column, past the owner value the
    /// consumer re-derived from Postgres (CSR round 12 §2.3). Every producer emits exact column
    /// names: the write RPCs canonicalise keys at entry, and enrichment and reconciliation
    /// republish <c>row_to_json</c>. A key that is not exact therefore names no column.
    /// </para>
    /// </summary>
    internal static List<KeyValuePair<string, JsonElement>> UpsertEntries(EngagementTableSchema schema, string payloadJson)
    {
        var row = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payloadJson)
            ?? new Dictionary<string, JsonElement>();

        var knownCols = schema.Columns
            .Select(c => c.Name)
            .Append(schema.KeyColumn.Name)
            .ToHashSet(StringComparer.Ordinal);

        return row
            .Where(kv => knownCols.Contains(kv.Key)
                      && kv.Value.ValueKind != JsonValueKind.Object
                      && kv.Value.ValueKind != JsonValueKind.Undefined)
            .ToList();
    }

    public async Task DeleteAsync(string tableName, string keyColumn, string keyValue, string tenantId)
    {
        if (!TenantIdentifier.IsValid(tenantId)) return;

        // CSR round 12 §7.3: a tenant never recorded as provisioned has no login and no rows to remove. A
        // delete for it (one whose create was dropped before projection) is a no-op, not a refused login.
        if (!await IsRecordedAsync(tenantId)) return;

        var qualifiedTable = TenantIdentifier.Qualify(TenantIdentifier.DatabaseName(tenantId), tableName);
        var sql = $"DELETE FROM {qualifiedTable} WHERE `{keyColumn}` = @key";
        StarRocksParameterGuard.EnsureAllPlaceholdersBound(sql, new { key = keyValue });
        await RunTenantScopedAsync("sr.execute", tenantId, sql, conn => conn.ExecuteAsync(sql, new { key = keyValue }));
    }

    public async Task EnsureTenantProvisionedAsync(string tenantId, EngagementTableSchema schema)
    {
        if (!TenantIdentifier.IsValid(tenantId)) return;

        // CSR round 11 §3: provisioning runs only under the provisioner login. An instance built without
        // one (the api role, which never provisions) refuses outright rather than falling back to the
        // query login, which holds no user_admin and no CREATE DATABASE.
        if (provisionerConnectionString is null)
            throw new InvalidOperationException(
                "Tenant provisioning needs the StarRocks provisioner connection string, and none was configured.");

        // CSR round 12 §7.1/§7.3: provisioning creates the tenant's own login and then records the tenant, so
        // it needs both halves of what a reader needs. Both are checked before connecting.
        var directory = provisionedTenants ?? throw new InvalidOperationException(NoTenantLoginsMessage);
        var loginPassword = TenantIdentifier.LoginPassword(
            tenantId, tenantLoginKey ?? throw new InvalidOperationException(NoTenantLoginsMessage));
        var loginName = TenantIdentifier.LoginName(tenantId);

        var dbName   = TenantIdentifier.DatabaseName(tenantId);
        var roleName = TenantIdentifier.RoleName(tenantId);
        var qualifiedTable = TenantIdentifier.Qualify(dbName, schema.TableName);
        var createTableDdl = StarRocksSchemaManager.BuildCreateTableDdl(schema, qualifiedTable);

        using var activity = Telemetry.Source.StartActivity("sr.provision_tenant", ActivityKind.Client);
        activity?.SetTag("db.system", "starrocks");
        activity?.SetTag("db.table", schema.TableName);

        await RunAsync(async () =>
        {
            await using var conn = CreateProvisionerConnection();
            await OpenProvisionerConnectionAsync(() => conn.OpenAsync());
            await conn.ExecuteAsync("SET ROLE user_admin");
            try
            {
                await conn.ExecuteAsync($"CREATE DATABASE IF NOT EXISTS `{dbName}`");
                await conn.ExecuteAsync($"CREATE ROLE IF NOT EXISTS `{roleName}`");
                await conn.ExecuteAsync($"GRANT SELECT, INSERT, UPDATE, DELETE ON `{dbName}`.* TO ROLE `{roleName}`");
                await conn.ExecuteAsync($"GRANT CREATE TABLE ON DATABASE `{dbName}` TO ROLE `{roleName}`");
                // CSR round 12 §7.1: the tenant's own login replaces the grant of the tenant role to
                // iverson_app. DEFAULT ROLE assigns the role created above and activates it at every login,
                // so no data connection runs SET ROLE. IF NOT EXISTS leaves an existing login as it is. The
                // caps are re-applied on every call, which SET PROPERTIES allows.
                await conn.ExecuteAsync(
                    $"CREATE USER IF NOT EXISTS '{loginName}'@'%' IDENTIFIED BY '{loginPassword}' DEFAULT ROLE '{roleName}'");
                await conn.ExecuteAsync($"ALTER USER '{loginName}'@'%' SET PROPERTIES ({TenantSessionProperties})");

                // CSR round 11 §3: the provisioner grants itself what the next steps need. Re-granting either
                // is a no-op.
                // - The tenant role, so `SET ROLE user_admin, <role>` below also works for a tenant whose role
                //   was created before the provisioner login existed.
                // - ALTER on the tenant's tables, for WidenLargeTextColumnsAsync's MODIFY COLUMN. Neither
                //   user_admin nor the tenant role carries ALTER. A direct grant is active whatever role is.
                await conn.ExecuteAsync($"GRANT `{roleName}` TO USER 'iverson_provisioner'@'%'");
                await conn.ExecuteAsync($"GRANT ALTER ON ALL TABLES IN DATABASE `{dbName}` TO USER 'iverson_provisioner'@'%'");

                // user_admin alone has no CREATE TABLE privilege on any database, including one it
                // just created — it's StarRocks's built-in user/role/grant-management role, not a
                // schema-DDL role, and (being a built-in system role) it cannot be granted one
                // either ("role user_admin is not mutable!"). The CREATE TABLE grant just issued
                // above went to the tenant role, not to user_admin, and a role granted mid-session
                // is not auto-activated — so the tenant role must be explicitly activated alongside
                // user_admin before CREATE TABLE can succeed. Verified empirically: single-role
                // `SET ROLE user_admin` throughout fails on CREATE TABLE with "Access denied ...
                // CREATE TABLE privilege(s) on DATABASE ... Current role(s): [user_admin]"; this
                // multi-role reactivation fixes it.
                await conn.ExecuteAsync($"SET ROLE user_admin, `{roleName}`");
                await conn.ExecuteAsync(createTableDdl);
                await WidenLargeTextColumnsAsync(conn, dbName, schema);
                return true;
            }
            finally
            {
                try
                {
                    await conn.ExecuteAsync("SET ROLE NONE");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "SET ROLE NONE failed while releasing a tenant-provisioning StarRocks connection");
                }
            }
        });

        // Only after the login exists: a recorded tenant is one readers connect for.
        await directory.RecordProvisionedAsync(tenantId);
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    public async Task<IEnumerable<dynamic>> SearchAsync(
        EngagementQuerySchema schema,
        SearchQuery? query,
        int page,
        int pageSize,
        IReadOnlyList<string>? fields = null,
        IReadOnlyList<JoinSpec>? joins = null,
        Func<string, EngagementQuerySchema?>? registry = null,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
    {
        // authz == null means the caller isn't going through Part A's authorization evaluation at
        // all (e.g. a unit test exercising raw SQL generation) — preserve today's unscoped behavior.
        // Production (ObjectSearchGrpcService) always passes a real authz dict and never reaches
        // here with a missing tenant value (it denies upstream first) — verified at plan-write time.
        if (authz is null)
        {
            var (unscopedSql, unscopedParam) = StarRocksQueryBuilder.BuildSearch(
                schema.TableName, schema, query, page, pageSize, fields, joins, registry, authz,
                limits: _queryLimits);
            return await QueryAsync<dynamic>(unscopedSql, unscopedParam);
        }

        var tenantId = authz.GetValueOrDefault(schema.TypeName)?.TenantValue;
        if (tenantId is null || !TenantIdentifier.IsValid(tenantId))
            return [];

        var (sql, param) = StarRocksQueryBuilder.BuildSearch(
            schema.TableName, schema, query, page, pageSize, fields, joins, registry, authz,
            TenantIdentifier.DatabaseName(tenantId), _queryLimits);

        if (!await IsRecordedAsync(tenantId))
            return [];

        try
        {
            return await RunTenantScopedAsync(
                "sr.query", tenantId, sql, conn => conn.QueryAsync<dynamic>(sql, param), IsExpectedMissingResourceError);
        }
        catch (Exception ex) when (IsExpectedMissingResourceError(ex))
        {
            return [];
        }
    }

    public async Task<AggregationResult?> AggregateAsync(
        EngagementQuerySchema schema,
        SearchQuery? query,
        AggregationDescriptor spec,
        SearchQuery? having = null,
        IReadOnlyList<JoinSpec>? joins = null,
        Func<string, EngagementQuerySchema?>? registry = null,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
    {
        if (spec.GroupByFields is { Count: > 1 })
            throw new EngagementQueryTranslationException(
                "Multi-key GROUP BY (group_by_fields with more than one entry) is not yet supported via the Aggregate RPC's result decoding; use a single field or wait for the GroupByRequest RPC.");

        List<dynamic> rows;
        if (authz is null)
        {
            var (unscopedSql, unscopedParam) = StarRocksQueryBuilder.BuildAggregate(
                schema.TableName, schema, query, spec, having, joins, registry, authz,
                limits: _queryLimits);
            rows = (await QueryAsync<dynamic>(unscopedSql, unscopedParam)).ToList();
        }
        else
        {
            var tenantId = authz.GetValueOrDefault(schema.TypeName)?.TenantValue;
            if (tenantId is null || !TenantIdentifier.IsValid(tenantId))
                return null;

            var (sql, param) = StarRocksQueryBuilder.BuildAggregate(
                schema.TableName, schema, query, spec, having, joins, registry, authz,
                TenantIdentifier.DatabaseName(tenantId), _queryLimits);

            if (!await IsRecordedAsync(tenantId))
                return null;

            try
            {
                rows = (await RunTenantScopedAsync(
                    "sr.query", tenantId, sql, conn => conn.QueryAsync<dynamic>(sql, param), IsExpectedMissingResourceError)).ToList();
            }
            catch (Exception ex) when (IsExpectedMissingResourceError(ex))
            {
                return null;
            }
        }

        switch (spec.Kind)
        {
            case AggregationKind.Terms:
            case AggregationKind.DateHistogram:
            case AggregationKind.Range:
            {
                var buckets = rows
                    .Select(r => (IDictionary<string, object>)r)
                    .Where(r => r.TryGetValue("bucket_key", out var k) && k is not null)
                    .Select(r => new AggregationBucket(
                        r["bucket_key"]?.ToString() ?? string.Empty,
                        Convert.ToInt64(r["doc_count"])))
                    .ToList();
                return new AggregationResult(spec.Name, spec.Kind, Buckets: buckets);
            }

            default:
            {
                if (rows.Count == 0) return new AggregationResult(spec.Name, spec.Kind, MetricValue: null);
                var row0 = (IDictionary<string, object>)rows[0];
                row0.TryGetValue("metric_val", out var val);
                return new AggregationResult(spec.Name, spec.Kind,
                    MetricValue: val is null ? null : Convert.ToDouble(val));
            }
        }
    }

    public async Task<IEnumerable<dynamic>> GroupByAsync(
        EngagementQuerySchema schema,
        GroupByRequest request,
        Func<string, EngagementQuerySchema?> registry,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
    {
        // authz == null means the caller isn't going through Part A's authorization evaluation at
        // all (e.g. a unit test exercising raw SQL generation) — preserve today's unscoped behavior.
        if (authz is null)
        {
            var (unscopedSql, unscopedParam) = StarRocksQueryBuilder.BuildGroupBy(
                schema.TableName, schema, request, registry, authz, limits: _queryLimits);
            return await QueryAsync<dynamic>(unscopedSql, unscopedParam);
        }

        var tenantId = authz.GetValueOrDefault(schema.TypeName)?.TenantValue;
        if (tenantId is null || !TenantIdentifier.IsValid(tenantId))
            return [];

        var (sql, param) = StarRocksQueryBuilder.BuildGroupBy(
            schema.TableName, schema, request, registry, authz, TenantIdentifier.DatabaseName(tenantId), _queryLimits);

        if (!await IsRecordedAsync(tenantId))
            return [];

        try
        {
            return await RunTenantScopedAsync(
                "sr.query", tenantId, sql, conn => conn.QueryAsync<dynamic>(sql, param), IsExpectedMissingResourceError);
        }
        catch (Exception ex) when (IsExpectedMissingResourceError(ex))
        {
            return [];
        }
    }

    public async Task<IEnumerable<dynamic>> PipelineAsync(
        EngagementQuerySchema schema,
        PipelineRequest request,
        Func<string, EngagementQuerySchema?> registry,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
    {
        if (authz is null)
        {
            var (unscopedSql, unscopedParam, unscopedLastCols) = StarRocksPipelineBuilder.Build(
                schema, request, registry, authz, limits: _queryLimits);
            var unscopedRows = await QueryAsync<dynamic>(unscopedSql, unscopedParam);
            return MaskPipelineRows(unscopedRows, unscopedLastCols);
        }

        var tenantId = authz.GetValueOrDefault(schema.TypeName)?.TenantValue;
        if (tenantId is null || !TenantIdentifier.IsValid(tenantId))
            return [];

        var (sql, param, lastCols) = StarRocksPipelineBuilder.Build(
            schema, request, registry, authz, TenantIdentifier.DatabaseName(tenantId), _queryLimits);

        if (!await IsRecordedAsync(tenantId))
            return [];

        try
        {
            var rows = await RunTenantScopedAsync(
                "sr.query", tenantId, sql, conn => conn.QueryAsync<dynamic>(sql, param), IsExpectedMissingResourceError);
            return MaskPipelineRows(rows, lastCols);
        }
        catch (Exception ex) when (IsExpectedMissingResourceError(ex))
        {
            return [];
        }
    }

    /// <summary>
    /// Layer 2 (post-fetch) masking for <see cref="PipelineAsync"/>: every intermediate/final CTE's
    /// actual physical columns can be broader than <paramref name="lastCols"/> (the base CTE and any
    /// unqualified "select *" passthrough both select every raw table column, restricted or not —
    /// see <see cref="StarRocksPipelineBuilder.Build"/>'s remarks) so each row must still be
    /// stripped down to exactly the tracked, already-authorization-filtered column set before it
    /// leaves <c>Iverson.StarRocks</c>. A no-op when <paramref name="lastCols"/> contains every
    /// column (the unrestricted/no-authz case). Extracted as its own internal static method — rather
    /// than left inline in <see cref="PipelineAsync"/> — specifically so this transformation has a
    /// unit-testable seam that doesn't require a live StarRocks connection (<see cref="QueryAsync{T}"/>
    /// is not virtual and this class is sealed, so <see cref="PipelineAsync"/> itself cannot be
    /// exercised without a real backend).
    /// </summary>
    internal static IEnumerable<dynamic> MaskPipelineRows(
        IEnumerable<dynamic> rows, IReadOnlyDictionary<string, string> lastCols) =>
        rows
            .Select(row =>
            {
                var dict = (IDictionary<string, object>)row;
                foreach (var key in dict.Keys.Where(k => !lastCols.ContainsKey(k)).ToList())
                    dict.Remove(key);
                return (dynamic)row;
            })
            .ToList();

    private static async Task<bool> CheckBackendAliveAsync(string connectionString, CancellationToken ct)
    {
        var probeConnectionString = new MySqlConnectionStringBuilder(connectionString) { Database = "" }.ToString();
        await using var conn = new MySqlConnection(probeConnectionString);
        await conn.OpenAsync(ct);
        return await EngagementHealthChecker.AnyBackendReadyAsync(conn, ct);
    }

    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? (object)l : el.GetDouble(),
        JsonValueKind.True   => true,
        JsonValueKind.False  => false,
        JsonValueKind.Null   => null,
        JsonValueKind.Array  => el.GetRawText(),
        _                    => el.GetRawText()
    };

    /// <summary>
    /// Widens any large-text column that is still narrower than the schema now asks for.
    ///
    /// <para><b>Why this is not optional.</b> The table above is created with
    /// <c>CREATE TABLE IF NOT EXISTS</c>, which never touches a table that already exists. A
    /// deployment provisioned before large text fields were widened therefore keeps its
    /// <c>varchar(65533)</c> columns forever — while the write path, reading the CURRENT schema,
    /// now accepts values up to a megabyte for exactly those fields. Without this the guard would
    /// wave through values the table cannot hold, which is the original defect back again and worse,
    /// because the write path would be asserting the value fits.</para>
    ///
    /// <para>Safe to run on every provisioning call, all three properties verified against a live
    /// 4.1.1: re-issuing <c>MODIFY COLUMN</c> for the type a column already has is accepted rather
    /// than an error; <c>information_schema</c> reports the current width, so the ALTER is only
    /// issued when it would actually change something; and StarRocks refuses to shorten a string
    /// column at all ("Cannot shorten string length"), so this can only ever widen.</para>
    /// </summary>
    private static async Task WidenLargeTextColumnsAsync(
        MySqlConnection conn, string dbName, EngagementTableSchema schema)
    {
        var wide = schema.Columns
            .Where(c => string.Equals(c.SrType, StarRocksLimits.WideTextColumnType, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Name)
            .ToList();

        if (wide.Count == 0) return;

        var current = (await conn.QueryAsync<(string Name, long? MaxLength)>(
            """
            SELECT COLUMN_NAME AS Name, CHARACTER_MAXIMUM_LENGTH AS MaxLength
            FROM information_schema.columns
            WHERE TABLE_SCHEMA = @Db AND TABLE_NAME = @Table
            """,
            new { Db = dbName, Table = schema.TableName }))
            .ToDictionary(r => r.Name, r => r.MaxLength, StringComparer.OrdinalIgnoreCase);

        foreach (var column in wide)
        {
            // Absent from information_schema means the column is not there at all — a different
            // problem, and not one a widening ALTER would fix. Leave it alone rather than guessing.
            if (!current.TryGetValue(column, out var maxLength) || maxLength is null) continue;
            if (maxLength >= StarRocksLimits.MaxVarcharBytes) continue;

            await conn.ExecuteAsync(
                $"ALTER TABLE `{dbName}`.`{schema.TableName}` " +
                $"MODIFY COLUMN `{column}` {StarRocksLimits.WideTextColumnType}");
        }
    }

}
