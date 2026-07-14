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

    public async Task UpsertAsync(StarRocksTableSchema schema, string payloadJson)
    {
        using var activity = Telemetry.Source.StartActivity("sr.upsert", ActivityKind.Client);
        activity?.SetTag("db.system", "starrocks");
        activity?.SetTag("db.table", schema.TableName);

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

        // StarRocks Primary Key model treats INSERT of an existing key as a FULL-ROW REPLACE:
        // any column absent from the INSERT list is reset to its default/null.
        // This is safe here because both ObjectPersistenceGrpcService.Update and
        // ObjectMappingGrpcService.Update call StructSerializer.SerializePayload on
        // request.Payload — which serialises the ENTIRE Struct the client sent —
        // and the API contract requires clients to supply the complete entity on Update.
        // If a partial-payload Update is ever introduced the producer must be changed first.
        var sql       = $"INSERT INTO `{schema.TableName}` ({colList}) VALUES ({paramList})";

        var param = new DynamicParameters();
        for (var i = 0; i < entries.Count; i++)
            param.Add($"p{i}", JsonElementToObject(entries[i].Value));

        await ExecuteAsync(sql, param);
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    public Task DeleteAsync(string tableName, string keyColumn, string keyValue) =>
        ExecuteAsync(
            $"DELETE FROM `{tableName}` WHERE `{keyColumn}` = @key",
            new { key = keyValue });

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
        var (sql, param) = StarRocksQueryBuilder.BuildSearch(
            schema.TableName, schema, query, page, pageSize, fields, joins, registry, authz);
        return await QueryAsync<dynamic>(sql, param);
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

        var (sql, param) = StarRocksQueryBuilder.BuildAggregate(
            schema.TableName, schema, query, spec, having, joins, registry, authz);

        var rows = (await QueryAsync<dynamic>(sql, param)).ToList();

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
        var (sql, param) = StarRocksQueryBuilder.BuildGroupBy(schema.TableName, schema, request, registry, authz);
        return await QueryAsync<dynamic>(sql, param);
    }

    public async Task<IEnumerable<dynamic>> PipelineAsync(
        StarRocksQuerySchema schema,
        PipelineRequest request,
        Func<string, StarRocksQuerySchema?> registry,
        IReadOnlyDictionary<string, AuthorizationConstraint>? authz = null)
    {
        var (sql, param) = StarRocksPipelineBuilder.Build(schema, request, registry, authz);
        return await QueryAsync<dynamic>(sql, param);
    }

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
