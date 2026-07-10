using System.Diagnostics;
using System.Text.Json;
using Iverson.Api.Consumers;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Embeddings;
using Iverson.Events;
using Iverson.Sql;
using Iverson.StarRocks;
using Iverson.Vector;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// The env-var configuration provider only folds "__" into ":" — it does not strip the
// single underscore in "WORKLOAD_ROLE" — so that env var lands in IConfiguration under the
// literal key "WORKLOAD_ROLE", not "WorkloadRole". Check both: the literal env var name (what
// Helm/docker-compose will actually set) and the PascalCase key (appsettings.json override).
var workloadRole = cfg["WORKLOAD_ROLE"] ?? cfg["WorkloadRole"] ?? "api";
if (workloadRole is not ("api" or "worker"))
    throw new InvalidOperationException($"WorkloadRole must be 'api' or 'worker', got '{workloadRole}'.");

// ── OpenTelemetry ──────────────────────────────────────────────────────────────
var otelEndpoint = cfg["Otel:Endpoint"] ?? "http://localhost:4317";

var resource = ResourceBuilder.CreateDefault()
    .AddService(serviceName: workloadRole == "worker" ? "Iverson.Worker" : "Iverson.Api", serviceVersion: "1.0.0")
    .AddAttributes(new Dictionary<string, object>
    {
        ["deployment.environment"] = builder.Environment.EnvironmentName
    });

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .SetResourceBuilder(resource)
        .SetSampler(new AlwaysOnSampler())
        .AddSource(
            "Iverson.Sql",
            "Iverson.StarRocks",
            "Iverson.Vector",
            "Iverson.Events",
            "Iverson.Embeddings",
            "Grpc.Net.Server")
        .AddAspNetCoreInstrumentation(o =>
        {
            o.RecordException = true;
            o.Filter = ctx => ctx.Request.Path != "/health" && ctx.Request.Path != "/health/live";  // skip noisy health checks
        })
        .AddHttpClientInstrumentation(o => o.RecordException = true)
        .AddOtlpExporter(o =>
        {
            o.Endpoint = new Uri(otelEndpoint);
            o.Protocol = OtlpExportProtocol.Grpc;
        }));

builder.Logging.AddOpenTelemetry(o =>
{
    o.SetResourceBuilder(resource);
    o.IncludeScopes = true;
    o.IncludeFormattedMessage = true;
    o.AddOtlpExporter(x =>
    {
        x.Endpoint = new Uri(otelEndpoint);
        x.Protocol = OtlpExportProtocol.Grpc;
    });
});

// ── Application services ───────────────────────────────────────────────────────
builder.Services.AddOpenApi();
builder.Services.AddGrpc();

builder.Services.AddPostgres(cfg.GetConnectionString("Postgres")
    ?? "Host=localhost;Port=5432;Database=iverson;Username=iverson;Password=iverson");

builder.Services.AddStarRocks(
    cfg.GetConnectionString("StarRocks")
    ?? "Server=localhost;Port=9030;Database=iverson;User Id=root;Password=;AllowPublicKeyRetrieval=true;",
    new StarRocksResilienceOptions
    {
        BackendReadyTimeout = TimeSpan.FromSeconds(cfg.GetValue("StarRocks:BackendReadyTimeoutSeconds", 120)),
        CircuitBreaker = new StarRocksCircuitBreakerOptions
        {
            FailureRatio      = cfg.GetValue("StarRocks:CircuitBreaker:FailureRatio", 0.5),
            MinimumThroughput = cfg.GetValue("StarRocks:CircuitBreaker:MinimumThroughput", 4),
            SamplingDuration  = TimeSpan.FromSeconds(cfg.GetValue("StarRocks:CircuitBreaker:SamplingDurationSeconds", 30)),
            BreakDuration     = TimeSpan.FromSeconds(cfg.GetValue("StarRocks:CircuitBreaker:BreakDurationSeconds", 15))
        }
    });

builder.Services.AddQdrant(
    cfg["Qdrant:Host"] ?? "localhost",
    int.Parse(cfg["Qdrant:Port"] ?? "6334"));

builder.Services.AddKafka(cfg);

builder.Services.AddSingleton<SchemaRegistry>();
builder.Services.AddSingleton<Iverson.Api.Reconciliation.ReconciliationService>();

builder.Services.AddEmbeddings(cfg);

// Defense-in-depth: ConsumerResilience.RunWithRestartAsync already catches and retries
// every exception these hosted services can throw, but if something ever escapes that
// wrapper (e.g. a bug in the wrapper itself), StopHost's default behavior would take down
// the entire API. Ignore means only the faulted hosted service stops — not the whole process.
builder.Services.Configure<Microsoft.Extensions.Hosting.HostOptions>(o =>
    o.BackgroundServiceExceptionBehavior = Microsoft.Extensions.Hosting.BackgroundServiceExceptionBehavior.Ignore);

if (workloadRole == "worker")
{
    builder.Services.AddHostedService<EngagementStoreConsumer>();
    builder.Services.AddHostedService<IntelligenceStoreConsumer>();
    builder.Services.AddHostedService<Iverson.Api.Reconciliation.DlqMonitorConsumer>();
    builder.Services.AddHostedService<Iverson.Api.Reconciliation.ReconciliationQueueWorker>();
}

// Registered for both roles (not gated on workloadRole): api itself runs multiple replicas
// in every non-local environment (values.yaml, api.replicas: 2), so a second api replica has
// the identical SchemaRegistry cache-coherence gap worker has — RegisterSchema only updates
// the calling process's own in-memory copy.
builder.Services.AddHostedService<Iverson.Api.Schema.SchemaRefreshWorker>();

// ── Middleware ─────────────────────────────────────────────────────────────────
var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseHttpsRedirection();

// Expose the W3C trace-id on every response so callers can correlate logs
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        if (Activity.Current?.TraceId is { } traceId)
            context.Response.Headers["X-Trace-Id"] = traceId.ToString();
        return Task.CompletedTask;
    });
    await next();
});

// ── Endpoints ──────────────────────────────────────────────────────────────────
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" })).WithName("HealthLive");

app.MapGet("/health", async (
    IPostgresQueryExecutor db,
    IStarRocksHealthCheck sr,
    IVectorSchemaManager vector,
    IEventProducer kafka) =>
{
    var pgTask     = db.QuerySingleOrDefaultAsync<int>("SELECT 1").ContinueWith(t => t.IsCompletedSuccessfully && t.Result == 1);
    var srTask     = sr.CheckHealthAsync();
    var vectorTask = vector.EnsureCollectionAsync("iverson-probe", 4).ContinueWith(t => t.IsCompletedSuccessfully);
    var kafkaTask  = kafka.ProduceAsync("iverson.health.probe", "probe", new { ts = DateTime.UtcNow })
                         .ContinueWith(t => t.IsCompletedSuccessfully);

    await Task.WhenAll(pgTask, srTask, vectorTask, kafkaTask);

    var srStatus = await srTask;
    var checks = new
    {
        postgres  = pgTask.Result,
        starrocks = srStatus == StarRocksHealthStatus.Healthy,
        qdrant    = vectorTask.Result,
        kafka     = kafkaTask.Result
    };

    // StarRocks "auth pending" is expected during a fresh install: the create-user post-install
    // hook can only run after --wait succeeds on this very readiness probe, so failing readiness
    // on AuthPending would deadlock every first install forever. It's still reported unhealthy
    // in the body (checks.starrocks stays false) for real observability — only the k8s-facing
    // readiness verdict (the HTTP status code) tolerates it.
    var readinessHealthy = checks.postgres && checks.qdrant && checks.kafka
        && srStatus != StarRocksHealthStatus.Unhealthy;
    var fullyHealthy = readinessHealthy && checks.starrocks;

    return readinessHealthy
        ? Results.Ok(new { status = fullyHealthy ? "healthy" : "degraded", checks })
        : Results.Json(new { status = "degraded", checks }, statusCode: 503);
})
.WithName("Health");

app.MapGet("/probe/sql", async (IPostgresQueryExecutor db) =>
{
    var result = await db.QuerySingleOrDefaultAsync<int>("SELECT 1");
    return Results.Ok(new { connected = result == 1, traceId = Activity.Current?.TraceId.ToString() });
}).WithName("ProbeSql");

app.MapGet("/probe/starrocks", async (IStarRocksHealthCheck sr) =>
{
    var healthy = await sr.IsHealthyAsync();
    return Results.Ok(new { connected = healthy, traceId = Activity.Current?.TraceId.ToString() });
}).WithName("ProbeStarRocks");

app.MapGet("/probe/vector", async (IVectorSchemaManager vector) =>
{
    await vector.EnsureCollectionAsync("iverson-probe", 4);
    return Results.Ok(new { connected = true, collection = "iverson-probe", traceId = Activity.Current?.TraceId.ToString() });
}).WithName("ProbeVector");

app.MapPost("/probe/kafka", async (IEventProducer producer) =>
{
    var traceId = Activity.Current?.TraceId.ToString();
    await producer.ProduceAsync("iverson.probe", "probe", new { timestamp = DateTime.UtcNow, traceId });
    return Results.Ok(new { produced = true, topic = "iverson.probe", traceId });
}).WithName("ProbeKafka");

app.MapPost("/admin/reconcile/{typeName}", async (
    string typeName,
    Iverson.Api.Reconciliation.ReconciliationService reconciliation) =>
{
    var count = await reconciliation.ReconcileTypeAsync(typeName);
    return count is null
        ? Results.NotFound(new { error = $"No schema registered for '{typeName}'" })
        : Results.Ok(new { reconciledCount = count, typeName });
}).WithName("Reconcile");

app.MapGet("/admin/dlq", async (IPostgresQueryExecutor db) =>
{
    var rows = await db.QueryAsync<DlqRow>(
        $"""
        SELECT "Id", "SourceTopic", "ConsumerGroup", "MessageKey", "ExceptionType",
               "ExceptionMessage", "Attempts", "FailedAt", "Replayed"
        FROM "{Iverson.Api.Reconciliation.DlqSchema.TableName}"
        WHERE "Replayed" = false
        ORDER BY "FailedAt" DESC
        LIMIT 200
        """, null);
    return Results.Ok(rows);
}).WithName("ListDlq");

app.MapPost("/admin/dlq/{id}/replay", async (Guid id, IPostgresQueryExecutor db, IEventProducer events) =>
{
    var row = await db.QuerySingleOrDefaultAsync<DlqReplayRow>(
        $"""
        SELECT "SourceTopic", "MessageKey", "MessageValue"
        FROM "{Iverson.Api.Reconciliation.DlqSchema.TableName}"
        WHERE "Id" = @Id AND "Replayed" = false
        """, new { Id = id });

    if (row is null) return Results.NotFound(new { error = $"No unreplayed DLQ row with id '{id}'" });

    await events.ProduceAsync(row.SourceTopic, row.MessageKey, row.MessageValue);

    await db.ExecuteAsync(
        $"""UPDATE "{Iverson.Api.Reconciliation.DlqSchema.TableName}" SET "Replayed" = true WHERE "Id" = @Id""",
        new { Id = id });

    return Results.Ok(new { replayed = true, id, topic = row.SourceTopic });
}).WithName("ReplayDlq");

// ── Schema hydration ───────────────────────────────────────────────────────────
await app.Services.GetRequiredService<IEmbeddingService>().InitializeAsync();
await app.Services.GetRequiredService<SchemaRegistry>().LoadAsync();
await app.Services.GetRequiredService<IPostgresSchemaManager>().ApplySchemaAsync(Iverson.Api.Reconciliation.ReconciliationSchema.Table);
await app.Services.GetRequiredService<IPostgresSchemaManager>().ApplySchemaAsync(Iverson.Api.Reconciliation.DlqSchema.Table);

// ── gRPC endpoints ─────────────────────────────────────────────────────────────
if (workloadRole == "api")
{
    app.MapGrpcService<ObjectMappingGrpcService>();
    app.MapGrpcService<ObjectPersistenceGrpcService>();
    app.MapGrpcService<ObjectRetrievalGrpcService>();
    app.MapGrpcService<ObjectSearchGrpcService>();
}

app.Lifetime.ApplicationStarted.Register(() =>
{
    app.Logger.LogInformation("Iverson.Api is available — all Kafka topic subscriptions initiated");
    app.Logger.LogInformation("OTel OTLP endpoint: {Endpoint}", otelEndpoint);
    app.Logger.LogInformation("Iverson.Api started in '{Role}' role", workloadRole);
});

app.Run();

// ── DTOs for minimal-API endpoints ──────────────────────────────────────────────
internal sealed record DlqRow(Guid Id, string SourceTopic, string ConsumerGroup, string MessageKey,
    string? ExceptionType, string? ExceptionMessage, int Attempts, DateTimeOffset FailedAt, bool Replayed);
internal sealed record DlqReplayRow(string SourceTopic, string MessageKey, string MessageValue);
