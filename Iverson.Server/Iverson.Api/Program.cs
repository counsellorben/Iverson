using System.Diagnostics;
using Iverson.Api.Consumers;
using Iverson.Api.Grpc;
using Iverson.Api.Schema;
using Iverson.Elasticsearch;
using Iverson.Embeddings;
using Iverson.Events;
using Iverson.Sql;
using Iverson.Vector;
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
        .AddSource(
            "Iverson.Sql",
            "Iverson.Elasticsearch",
            "Iverson.Vector",
            "Iverson.Events",
            "Iverson.Embeddings")
        .AddAspNetCoreInstrumentation(o =>
        {
            o.RecordException = true;
            o.Filter = ctx => ctx.Request.Path != "/health";  // skip noisy health checks
        })
        .AddHttpClientInstrumentation(o => o.RecordException = true)
        .AddOtlpExporter(o => o.Endpoint = new Uri(otelEndpoint)));

builder.Logging.AddOpenTelemetry(o =>
{
    o.SetResourceBuilder(resource);
    o.IncludeScopes = true;
    o.IncludeFormattedMessage = true;
    o.AddOtlpExporter(x => x.Endpoint = new Uri(otelEndpoint));
});

// ── Application services ───────────────────────────────────────────────────────
builder.Services.AddOpenApi();
builder.Services.AddGrpc();

builder.Services.AddPostgres(cfg.GetConnectionString("Postgres")
    ?? "Host=localhost;Port=5432;Database=iverson;Username=iverson;Password=iverson");

builder.Services.AddElasticsearch(
    cfg["Elasticsearch:Url"] ?? "http://localhost:9200");

builder.Services.AddQdrant(
    cfg["Qdrant:Host"] ?? "localhost",
    int.Parse(cfg["Qdrant:Port"] ?? "6334"));

builder.Services.AddKafka(
    cfg["Kafka:BootstrapServers"] ?? "localhost:9092");

builder.Services.AddSingleton<SchemaRegistry>();

builder.Services.AddEmbeddings(cfg);

builder.Services.AddHostedService<RecordStoreConsumer>();
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
    IElasticsearchService es,
    IVectorService vector,
    IEventProducer kafka) =>
{
    var pgTask     = db.QuerySingleOrDefaultAsync<int>("SELECT 1").ContinueWith(t => t.IsCompletedSuccessfully && t.Result == 1);
    var esTask     = es.IndexExistsAsync("iverson-probe").ContinueWith(t => t.IsCompletedSuccessfully);
    var vectorTask = vector.EnsureCollectionAsync("iverson-probe", 4).ContinueWith(t => t.IsCompletedSuccessfully);
    var kafkaTask  = kafka.ProduceAsync("iverson-health-probe", "probe", new { ts = DateTime.UtcNow })
                         .ContinueWith(t => t.IsCompletedSuccessfully);

    await Task.WhenAll(pgTask, esTask, vectorTask, kafkaTask);

    var checks = new
    {
        postgres      = pgTask.Result,
        elasticsearch = esTask.Result,
        qdrant        = vectorTask.Result,
        kafka         = kafkaTask.Result
    };

    var allHealthy = checks.postgres && checks.elasticsearch && checks.qdrant && checks.kafka;

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

app.MapGet("/probe/elasticsearch", async (IElasticsearchService es) =>
{
    var exists = await es.IndexExistsAsync("iverson-probe");
    return Results.Ok(new { connected = true, probeIndexExists = exists, traceId = Activity.Current?.TraceId.ToString() });
}).WithName("ProbeElasticsearch");

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

// ── Schema hydration ───────────────────────────────────────────────────────────
await app.Services.GetRequiredService<IEmbeddingService>().InitializeAsync();
await app.Services.GetRequiredService<SchemaRegistry>().LoadAsync();

// ── gRPC endpoints ─────────────────────────────────────────────────────────────
app.MapGrpcService<ObjectMappingGrpcService>();
app.MapGrpcService<ObjectPersistenceGrpcService>();
app.MapGrpcService<ObjectRetrievalGrpcService>();
app.MapGrpcService<ObjectSearchGrpcService>();

app.Lifetime.ApplicationStarted.Register(() =>
    app.Logger.LogInformation("Iverson.Api is available — all Kafka topic subscriptions initiated"));

app.Run();
