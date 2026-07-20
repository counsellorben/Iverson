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

    private async Task<T> RunTenantScopedAsync<T>(
        string activityName, string tenantId, string sql, Func<MySqlConnection, Task<T>> operation)
    {
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
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.RecordException(ex);
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

        return await RunTenantScopedAsync("sr.query", tenantId, sql, conn => conn.QueryAsync<dynamic>(sql, param));
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
            rows = (await RunTenantScopedAsync("sr.query", tenantId, sql, conn => conn.QueryAsync<dynamic>(sql, param))).ToList();
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

        return await RunTenantScopedAsync("sr.query", tenantId, sql, conn => conn.QueryAsync<dynamic>(sql, param));
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
        var rows = await RunTenantScopedAsync("sr.query", tenantId, sql, conn => conn.QueryAsync<dynamic>(sql, param));
        return MaskPipelineRows(rows, lastCols);
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
