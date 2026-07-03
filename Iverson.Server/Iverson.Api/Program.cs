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

// ── OpenTelemetry ──────────────────────────────────────────────────────────────
var otelEndpoint = cfg["Otel:Endpoint"] ?? "http://localhost:4317";

var resource = ResourceBuilder.CreateDefault()
    .AddService(serviceName: "Iverson.Api", serviceVersion: "1.0.0")
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
            o.Filter = ctx => ctx.Request.Path != "/health";  // skip noisy health checks
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
    ?? "Server=localhost;Port=9030;Database=iverson;User Id=root;Password=;AllowPublicKeyRetrieval=true;");

builder.Services.AddQdrant(
    cfg["Qdrant:Host"] ?? "localhost",
    int.Parse(cfg["Qdrant:Port"] ?? "6334"));

builder.Services.AddKafka(
    cfg["Kafka:BootstrapServers"] ?? "localhost:9092");

builder.Services.AddSingleton<SchemaRegistry>();
builder.Services.AddSingleton<Iverson.Api.Reconciliation.ReconciliationService>();

// Overrides AddKafka's default NullFailedPublishSink registration above — the container
// resolves the last registration for a single-implementation constructor injection, so this
// real sink (persists to the reconciliation queue table) is what IFailedPublishSink resolves to.
builder.Services.AddSingleton<Iverson.Events.IFailedPublishSink, Iverson.Api.Reconciliation.PostgresFailedPublishSink>();
builder.Services.AddHostedService<Iverson.Api.Reconciliation.ReconciliationQueueWorker>();

builder.Services.AddEmbeddings(cfg);

// Defense-in-depth: ConsumerResilience.RunWithRestartAsync already catches and retries
// every exception these hosted services can throw, but if something ever escapes that
// wrapper (e.g. a bug in the wrapper itself), StopHost's default behavior would take down
// the entire API. Ignore means only the faulted hosted service stops — not the whole process.
builder.Services.Configure<Microsoft.Extensions.Hosting.HostOptions>(o =>
    o.BackgroundServiceExceptionBehavior = Microsoft.Extensions.Hosting.BackgroundServiceExceptionBehavior.Ignore);

builder.Services.AddHostedService<EngagementStoreConsumer>();
builder.Services.AddHostedService<IntelligenceStoreConsumer>();

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
app.MapGet("/health", async (
    IPostgresRepository db,
    IStarRocksRepository sr,
    IVectorService vector,
    IEventProducer kafka) =>
{
    var pgTask     = db.QuerySingleOrDefaultAsync<int>("SELECT 1").ContinueWith(t => t.IsCompletedSuccessfully && t.Result == 1);
    var srTask     = sr.IsHealthyAsync();
    var vectorTask = vector.EnsureCollectionAsync("iverson-probe", 4).ContinueWith(t => t.IsCompletedSuccessfully);
    var kafkaTask  = kafka.ProduceAsync("iverson-health-probe", "probe", new { ts = DateTime.UtcNow })
                         .ContinueWith(t => t.IsCompletedSuccessfully);

    await Task.WhenAll(pgTask, srTask, vectorTask, kafkaTask);

    var checks = new
    {
        postgres  = pgTask.Result,
        starrocks = await srTask,
        qdrant    = vectorTask.Result,
        kafka     = kafkaTask.Result
    };

    var allHealthy = checks.postgres && checks.starrocks && checks.qdrant && checks.kafka;

    return allHealthy
        ? Results.Ok(new { status = "healthy", checks })
        : Results.Json(new { status = "degraded", checks }, statusCode: 503);
})
.WithName("Health");

app.MapGet("/probe/sql", async (IPostgresRepository db) =>
{
    var result = await db.QuerySingleOrDefaultAsync<int>("SELECT 1");
    return Results.Ok(new { connected = result == 1, traceId = Activity.Current?.TraceId.ToString() });
}).WithName("ProbeSql");

app.MapGet("/probe/starrocks", async (IStarRocksRepository sr) =>
{
    var healthy = await sr.IsHealthyAsync();
    return Results.Ok(new { connected = healthy, traceId = Activity.Current?.TraceId.ToString() });
}).WithName("ProbeStarRocks");

app.MapGet("/probe/vector", async (IVectorService vector) =>
{
    await vector.EnsureCollectionAsync("iverson-probe", 4);
    return Results.Ok(new { connected = true, collection = "iverson-probe", traceId = Activity.Current?.TraceId.ToString() });
}).WithName("ProbeVector");

app.MapPost("/probe/kafka", async (IEventProducer producer) =>
{
    var traceId = Activity.Current?.TraceId.ToString();
    await producer.ProduceAsync("iverson-probe", "probe", new { timestamp = DateTime.UtcNow, traceId });
    return Results.Ok(new { produced = true, topic = "iverson-probe", traceId });
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

// ── Schema hydration ───────────────────────────────────────────────────────────
await app.Services.GetRequiredService<IEmbeddingService>().InitializeAsync();
await app.Services.GetRequiredService<SchemaRegistry>().LoadAsync();
await app.Services.GetRequiredService<IPostgresRepository>().ApplySchemaAsync(Iverson.Api.Reconciliation.ReconciliationSchema.Table);

// ── gRPC endpoints ─────────────────────────────────────────────────────────────
app.MapGrpcService<ObjectMappingGrpcService>();
app.MapGrpcService<ObjectPersistenceGrpcService>();
app.MapGrpcService<ObjectRetrievalGrpcService>();
app.MapGrpcService<ObjectSearchGrpcService>();

app.Lifetime.ApplicationStarted.Register(() =>
{
    app.Logger.LogInformation("Iverson.Api is available — all Kafka topic subscriptions initiated");
    app.Logger.LogInformation("OTel OTLP endpoint: {Endpoint}", otelEndpoint);
});

app.Run();
