using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Iverson.Client.Contracts;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Polly;
using Polly.CircuitBreaker;

namespace Iverson.StarRocks;

public sealed class StarRocksRepository(
    string connectionString,
    ILogger<StarRocksRepository> logger,
    StarRocksResilienceOptions? resilienceOptions = null)
    : IEngagementStoreQueryExecutor, IEngagementStoreEntityStore, IEngagementStoreSearchService
{
    private readonly StarRocksResilienceOptions _resilience = resilienceOptions ?? StarRocksResilienceOptions.Default;

    private readonly StarRocksReadinessGate _readinessGate = new(
        ct => CheckBackendAliveAsync(connectionString, ct),
        (resilienceOptions ?? StarRocksResilienceOptions.Default).BackendReadyTimeout);

    private readonly ResiliencePipeline _pipeline =
        StarRocksResiliencePipelineFactory.Build(
            (resilienceOptions ?? StarRocksResilienceOptions.Default).CircuitBreaker, logger);

    private MySqlConnection CreateConnection() => new(connectionString);

    // ── Resilience chokepoint ───────────────────────────────────────────────────
    // Every real StarRocks operation routes through here: first the one-time cold-start
    // gate (near-instant no-op after the first success), then the circuit-breaker+retry
    // pipeline for ongoing protection against a backend that later goes unhealthy.
    private async Task<T> RunAsync<T>(Func<Task<T>> operation)
    {
        await _readinessGate.EnsureReadyAsync().ConfigureAwait(false);

        try
        {
            return await _pipeline
                .ExecuteAsync(async _ => await operation().ConfigureAwait(false))
                .ConfigureAwait(false);
        }
        catch (BrokenCircuitException ex)
        {
            throw new StarRocksNotReadyException(
                "StarRocks is currently unavailable (circuit breaker open).", ex);
        }
    }

    public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null)
    {
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

    // Two distinct "no data yet" shapes on the read path, both treated as fail-closed-empty
    // rather than propagated as exceptions:
    //
    // 1. Unprovisioned tenant: a tenant that has never written ANY data was never lazily
    //    provisioned by EnsureTenantProvisionedAsync, so its `role_tenant_{id}` either doesn't
    //    exist yet or was never granted to `iverson_app`. `SET ROLE` inside RunTenantScopedAsync
    //    then fails outright. Verified empirically against a live starrocks/allin1-ubuntu
    //    container: both "role doesn't exist" and "role exists but isn't granted to this user"
    //    raise MySqlConnector.MySqlException with ErrorCode == MySqlErrorCode.ParseError
    //    (StarRocks reuses MySQL's generic 1064 syntax-error code for this) and a message
    //    containing either "cannot find role" or "is not granted to".
    //
    // 2. Unwritten type within a provisioned tenant: EnsureTenantProvisionedAsync creates exactly
    //    one table per call (on that type's own first write), not every table a tenant will ever
    //    use — so a tenant that has written type A but queries type B (never written) has a
    //    perfectly valid role/database, but the specific table doesn't exist. Verified
    //    empirically against the same container: querying a nonexistent table under a database
    //    the active role legitimately has grants on raises MySqlException with ErrorCode 5502
    //    (SqlState 42602 — StarRocks-specific, no named MySqlErrorCode member) and a message
    //    containing "Unknown table". Deliberately distinct from — and must never accidentally
    //    match — two adjacent error shapes that must keep propagating as real failures: querying
    //    a wrong/nonexistent *database* name (a real bug, e.g. a tenant-ID-to-database-name
    //    mapping defect) raises a different message, "Unknown database" (ErrorCode 5501); and an
    //    actual permissions problem on a table that legitimately exists raises yet another
    //    message, "Access denied ... privilege(s) on TABLE ..." (ErrorCode 5203). Confirmed both
    //    do NOT contain "Unknown table" and so are correctly left unmatched here.
    //
    // Both cases are matched here as specific, verified StarRocks/MySqlConnector error shapes
    // (not a bare catch) so the 4 read paths can treat them the same as every other "no data"
    // case in this repository (invalid tenant ID, missing tenant claim: both already return
    // empty), while any other StarRocks failure (real outage, network issue, a genuinely
    // different privilege or schema problem) still propagates as an exception. Deliberately
    // scoped to the READ path only — UpsertAsync/DeleteAsync must keep throwing, since they're
    // only ever called after EnsureTenantProvisionedAsync has already run for that exact type in
    // the same code path (EngagementStoreConsumer), so hitting either shape there would mean a
    // real invariant violation, not a legitimate empty tenant.
    private static bool IsExpectedMissingResourceError(Exception ex) =>
        ex is MySqlException mex &&
        ((mex.ErrorCode == MySqlErrorCode.ParseError &&
          (mex.Message.Contains("cannot find role", StringComparison.OrdinalIgnoreCase) ||
           mex.Message.Contains("is not granted to", StringComparison.OrdinalIgnoreCase))) ||
         (mex.ErrorCode == (MySqlErrorCode)5502 &&
          mex.Message.Contains("Unknown table", StringComparison.OrdinalIgnoreCase)));

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
        // forgets that check fails loudly and immediately, rather than splicing an unvalidated
        // tenantId straight into the `SET ROLE` statement below.
        if (!TenantIdentifier.IsValid(tenantId))
            throw new ArgumentException($"Invalid tenant ID: '{tenantId}'", nameof(tenantId));

        using var activity = Telemetry.Source.StartActivity(activityName, ActivityKind.Client);
        activity?.SetTag("db.system", "starrocks");
        activity?.SetTag("db.statement", sql);

        try
        {
            var result = await RunAsync(async () =>
            {
                await using var conn = CreateConnection();
                await conn.OpenAsync();
                await conn.ExecuteAsync($"SET ROLE `{TenantIdentifier.RoleName(tenantId)}`");
                try
                {
                    return await operation(conn);
                }
                finally
                {
                    try
                    {
                        await conn.ExecuteAsync("SET ROLE NONE");
                    }
                    catch (Exception ex)
                    {
                        // The connection is being disposed either way; a broken connection here must
                        // never replace the operation's own result or exception — same discipline as
                        // PostgresRepository.RunTenantScopedAsync's rollback-failure handling.
                        logger.LogWarning(ex, "SET ROLE NONE failed while releasing a tenant-scoped StarRocks connection");
                    }
                }
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

    public async Task UpsertAsync(StarRocksTableSchema schema, string payloadJson, string tenantId)
    {
        using var activity = Telemetry.Source.StartActivity("sr.upsert", ActivityKind.Client);
        activity?.SetTag("db.system", "starrocks");
        activity?.SetTag("db.table", schema.TableName);

        if (!TenantIdentifier.IsValid(tenantId)) return;

        var row = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payloadJson)
            ?? new Dictionary<string, JsonElement>();

        var knownCols = schema.Columns
            .Select(c => c.Name)
            .Append(schema.KeyColumn.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var entries = row
            .Where(kv => knownCols.Contains(kv.Key)
                      && kv.Value.ValueKind != JsonValueKind.Object
                      && kv.Value.ValueKind != JsonValueKind.Undefined)
            .ToList();

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

        await RunTenantScopedAsync("sr.execute", tenantId, sql, conn => conn.ExecuteAsync(sql, param));
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    public async Task DeleteAsync(string tableName, string keyColumn, string keyValue, string tenantId)
    {
        if (!TenantIdentifier.IsValid(tenantId)) return;

        var qualifiedTable = TenantIdentifier.Qualify(TenantIdentifier.DatabaseName(tenantId), tableName);
        var sql = $"DELETE FROM {qualifiedTable} WHERE `{keyColumn}` = @key";
        await RunTenantScopedAsync("sr.execute", tenantId, sql, conn => conn.ExecuteAsync(sql, new { key = keyValue }));
    }

    public async Task EnsureTenantProvisionedAsync(string tenantId, StarRocksTableSchema schema)
    {
        if (!TenantIdentifier.IsValid(tenantId)) return;

        var dbName   = TenantIdentifier.DatabaseName(tenantId);
        var roleName = TenantIdentifier.RoleName(tenantId);
        var qualifiedTable = TenantIdentifier.Qualify(dbName, schema.TableName);
        var createTableDdl = StarRocksSchemaManager.BuildCreateTableDdl(schema, qualifiedTable);

        using var activity = Telemetry.Source.StartActivity("sr.provision_tenant", ActivityKind.Client);
        activity?.SetTag("db.system", "starrocks");
        activity?.SetTag("db.table", schema.TableName);

        await RunAsync(async () =>
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync();
            await conn.ExecuteAsync("SET ROLE user_admin");
            try
            {
                await conn.ExecuteAsync($"CREATE DATABASE IF NOT EXISTS `{dbName}`");
                await conn.ExecuteAsync($"CREATE ROLE IF NOT EXISTS `{roleName}`");
                await conn.ExecuteAsync($"GRANT SELECT, INSERT, UPDATE, DELETE ON `{dbName}`.* TO ROLE `{roleName}`");
                await conn.ExecuteAsync($"GRANT CREATE TABLE ON DATABASE `{dbName}` TO ROLE `{roleName}`");
                await conn.ExecuteAsync($"GRANT `{roleName}` TO USER 'iverson_app'@'%'");

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
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    public async Task<IEnumerable<dynamic>> SearchAsync(
        StarRocksQuerySchema schema,
        SearchQuery? query,
        int page,
        int pageSize,
        IReadOnlyList<string>? fields = null,
        IReadOnlyList<JoinSpec>? joins = null,
        Func<string, StarRocksQuerySchema?>? registry = null,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
    {
        // authz == null means the caller isn't going through Part A's authorization evaluation at
        // all (e.g. a unit test exercising raw SQL generation) — preserve today's unscoped behavior.
        // Production (ObjectSearchGrpcService) always passes a real authz dict and never reaches
        // here with a missing tenant value (it denies upstream first) — verified at plan-write time.
        if (authz is null)
        {
            var (unscopedSql, unscopedParam) = StarRocksQueryBuilder.BuildSearch(
                schema.TableName, schema, query, page, pageSize, fields, joins, registry, authz);
            return await QueryAsync<dynamic>(unscopedSql, unscopedParam);
        }

        var tenantId = authz.GetValueOrDefault(schema.TypeName)?.TenantValue;
        if (tenantId is null || !TenantIdentifier.IsValid(tenantId))
            return [];

        var (sql, param) = StarRocksQueryBuilder.BuildSearch(
            schema.TableName, schema, query, page, pageSize, fields, joins, registry, authz,
            TenantIdentifier.DatabaseName(tenantId));

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
        StarRocksQuerySchema schema,
        SearchQuery? query,
        AggregationDescriptor spec,
        SearchQuery? having = null,
        IReadOnlyList<JoinSpec>? joins = null,
        Func<string, StarRocksQuerySchema?>? registry = null,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
    {
        if (spec.GroupByFields is { Count: > 1 })
            throw new StarRocksQueryTranslationException(
                "Multi-key GROUP BY (group_by_fields with more than one entry) is not yet supported via the Aggregate RPC's result decoding; use a single field or wait for the GroupByRequest RPC.");

        List<dynamic> rows;
        if (authz is null)
        {
            var (unscopedSql, unscopedParam) = StarRocksQueryBuilder.BuildAggregate(
                schema.TableName, schema, query, spec, having, joins, registry, authz);
            rows = (await QueryAsync<dynamic>(unscopedSql, unscopedParam)).ToList();
        }
        else
        {
            var tenantId = authz.GetValueOrDefault(schema.TypeName)?.TenantValue;
            if (tenantId is null || !TenantIdentifier.IsValid(tenantId))
                return null;

            var (sql, param) = StarRocksQueryBuilder.BuildAggregate(
                schema.TableName, schema, query, spec, having, joins, registry, authz,
                TenantIdentifier.DatabaseName(tenantId));
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
        StarRocksQuerySchema schema,
        GroupByRequest request,
        Func<string, StarRocksQuerySchema?> registry,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
    {
        // authz == null means the caller isn't going through Part A's authorization evaluation at
        // all (e.g. a unit test exercising raw SQL generation) — preserve today's unscoped behavior.
        if (authz is null)
        {
            var (unscopedSql, unscopedParam) = StarRocksQueryBuilder.BuildGroupBy(schema.TableName, schema, request, registry, authz);
            return await QueryAsync<dynamic>(unscopedSql, unscopedParam);
        }

        var tenantId = authz.GetValueOrDefault(schema.TypeName)?.TenantValue;
        if (tenantId is null || !TenantIdentifier.IsValid(tenantId))
            return [];

        var (sql, param) = StarRocksQueryBuilder.BuildGroupBy(
            schema.TableName, schema, request, registry, authz, TenantIdentifier.DatabaseName(tenantId));

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
        StarRocksQuerySchema schema,
        PipelineRequest request,
        Func<string, StarRocksQuerySchema?> registry,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
    {
        if (authz is null)
        {
            var (unscopedSql, unscopedParam, unscopedLastCols) = StarRocksPipelineBuilder.Build(schema, request, registry, authz);
            var unscopedRows = await QueryAsync<dynamic>(unscopedSql, unscopedParam);
            return MaskPipelineRows(unscopedRows, unscopedLastCols);
        }

        var tenantId = authz.GetValueOrDefault(schema.TypeName)?.TenantValue;
        if (tenantId is null || !TenantIdentifier.IsValid(tenantId))
            return [];

        var (sql, param, lastCols) = StarRocksPipelineBuilder.Build(schema, request, registry, authz, TenantIdentifier.DatabaseName(tenantId));
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
        return await StarRocksHealthChecker.AnyBackendAliveAsync(conn, ct);
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
}
