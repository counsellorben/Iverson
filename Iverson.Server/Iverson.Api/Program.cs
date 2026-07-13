using System.Diagnostics;
using System.Text.Json;
using Iverson.Api;
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
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

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
        }))
    .WithMetrics(metrics => metrics
        .SetResourceBuilder(resource)
        .AddMeter("Iverson.Events", Iverson.Api.Reconciliation.ReconciliationTelemetry.MeterName)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddPrometheusExporter());

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

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = cfg["Authentication:Authority"];
        options.TokenValidationParameters.ValidAudiences = cfg.GetSection("Authentication:ValidAudiences").Get<string[]>();
        // This entire deployment is plaintext h2c/HTTP with no TLS anywhere (see otelEndpoint,
        // Kafka__BootstrapServers, etc. above) — Authentication:Authority points at Authentik's
        // OIDC discovery endpoint over a plain http:// URL. RequireHttpsMetadata defaults to
        // true in ASP.NET Core, which would make OIDC metadata discovery hard-fail against that
        // plaintext authority at startup/first-token-validation. Disabling it here matches the
        // rest of this deployment's no-TLS posture.
        options.RequireHttpsMetadata = false;
        // Without this, ASP.NET Core's default claim-type mapping silently renames the "sub"
        // claim to ClaimTypes.NameIdentifier (a legacy WS-Federation-era remapping table that
        // JwtBearerOptions.MapInboundClaims applies by default) — ActingUserInterceptor's
        // FindFirst("sub") would then always return null. Confirmed: this repo's only other
        // direct-claim-read code (OperatorAuthorizationPolicy) reads "groups"/"scope", neither
        // of which is in that remapping table, which is why this was never hit before.
        options.MapInboundClaims = false;
    })
    .AddJwtBearer("ActingUser", options =>
    {
        options.Authority = cfg["Authentication:ActingUser:Authority"];
        options.TokenValidationParameters.ValidAudiences = cfg.GetSection("Authentication:ActingUser:ValidAudiences").Get<string[]>();
        options.RequireHttpsMetadata = false;
        options.MapInboundClaims = false;
        // gRPC metadata IS the HTTP/2 header set — same mechanism the default scheme already
        // relies on for the "authorization" key. This scheme reads a different key so it
        // doesn't collide with the service credential on the same call.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                // "x-acting-user-authorization" is the Global Constraints' metadata key,
                // also defined as ActingUserInterceptor.MetadataKey (Task 2) — kept as a
                // literal here rather than referencing that constant so this task builds
                // standalone without a forward dependency on Task 2's file.
                var header = context.Request.Headers["x-acting-user-authorization"].ToString();
                if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    context.Token = header["Bearer ".Length..];
                else
                    context.NoResult();
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
    options.AddPolicy("Operator", policy => policy.RequireAssertion(context =>
        OperatorAuthorizationPolicy.IsSatisfiedBy(
            context.User.FindAll("groups").Select(c => c.Value),
            context.User.FindFirst("scope")?.Value)));
});

builder.Services.AddScoped<IActingUserAccessor, ActingUserAccessor>();

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
builder.Services.AddSingleton<IRelationValidator, RelationValidator>();
builder.Services.AddSingleton<IEntityKeyAccessor, EntityKeyAccessor>();
builder.Services.AddSingleton<IOutboxWriter>(sp => new OutboxWriter(
    Iverson.Api.Reconciliation.ReconciliationSchema.TableName,
    sp.GetRequiredService<IRecordStoreQueryExecutor>(),
    sp.GetRequiredService<IRecordStoreTransactionRunner>()));
builder.Services.AddSingleton<IReconciliationQueueRepository>(sp => new ReconciliationQueueRepository(
    Iverson.Api.Reconciliation.ReconciliationSchema.TableName,
    sp.GetRequiredService<IRecordStoreQueryExecutor>()));
builder.Services.AddSingleton<IDlqRepository>(sp => new DlqRepository(
    Iverson.Api.Reconciliation.DlqSchema.TableName,
    sp.GetRequiredService<IRecordStoreQueryExecutor>()));
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
    builder.Services.AddHostedService<Iverson.Api.Reconciliation.DlqBacklogGaugeWorker>();
}

// Registered for both roles (not gated on workloadRole): api itself runs multiple replicas
// in every non-local environment (values.yaml, api.replicas: 2), so a second api replica has
// the identical SchemaRegistry cache-coherence gap worker has — RegisterSchema only updates
// the calling process's own in-memory copy.
builder.Services.AddHostedService<Iverson.Api.Schema.SchemaRefreshWorker>();

// ── Middleware ─────────────────────────────────────────────────────────────────
var app = builder.Build();
app.MapPrometheusScrapingEndpoint().AllowAnonymous();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

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
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" })).WithName("HealthLive").AllowAnonymous();

app.MapGet("/health", async (
    IRecordStoreQueryExecutor db,
    IEngagementStoreHealthCheck sr,
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

    var readiness = ReadinessPolicy.Evaluate(checks.postgres, srStatus, checks.qdrant, checks.kafka);

    return readiness.Ready
        ? Results.Ok(new { status = readiness.FullyHealthy ? "healthy" : "degraded", checks })
        : Results.Json(new { status = "degraded", checks }, statusCode: 503);
})
.WithName("Health")
.AllowAnonymous();

app.MapGet("/probe/sql", async (IRecordStoreQueryExecutor db) =>
{
    var result = await db.QuerySingleOrDefaultAsync<int>("SELECT 1");
    return Results.Ok(new { connected = result == 1, traceId = Activity.Current?.TraceId.ToString() });
}).WithName("ProbeSql").AllowAnonymous();

app.MapGet("/probe/starrocks", async (IEngagementStoreHealthCheck sr) =>
{
    var healthy = await sr.IsHealthyAsync();
    return Results.Ok(new { connected = healthy, traceId = Activity.Current?.TraceId.ToString() });
}).WithName("ProbeStarRocks").AllowAnonymous();

app.MapGet("/probe/vector", async (IVectorSchemaManager vector) =>
{
    await vector.EnsureCollectionAsync("iverson-probe", 4);
    return Results.Ok(new { connected = true, collection = "iverson-probe", traceId = Activity.Current?.TraceId.ToString() });
}).WithName("ProbeVector").AllowAnonymous();

app.MapPost("/probe/kafka", async (IEventProducer producer) =>
{
    var traceId = Activity.Current?.TraceId.ToString();
    await producer.ProduceAsync("iverson.probe", "probe", new { timestamp = DateTime.UtcNow, traceId });
    return Results.Ok(new { produced = true, topic = "iverson.probe", traceId });
}).WithName("ProbeKafka").AllowAnonymous();

app.MapPost("/admin/reconcile/{typeName}", async (
    string typeName,
    Iverson.Api.Reconciliation.ReconciliationService reconciliation) =>
{
    var count = await reconciliation.ReconcileTypeAsync(typeName);
    return count is null
        ? Results.NotFound(new { error = $"No schema registered for '{typeName}'" })
        : Results.Ok(new { reconciledCount = count, typeName });
}).WithName("Reconcile").RequireAuthorization("Operator");

app.MapGet("/admin/dlq", async (IDlqRepository dlq) =>
{
    var rows = await dlq.ListUnreplayedAsync(200);
    return Results.Ok(rows);
}).WithName("ListDlq").RequireAuthorization("Operator");

app.MapPost("/admin/dlq/{id}/replay", async (Guid id, IDlqRepository dlq, IEventProducer events) =>
{
    var row = await dlq.GetUnreplayedByIdAsync(id);
    if (row is null) return Results.NotFound(new { error = $"No unreplayed DLQ row with id '{id}'" });

    await events.ProduceAsync(row.SourceTopic, row.MessageKey, row.MessageValue);
    await dlq.MarkReplayedAsync(id);

    return Results.Ok(new { replayed = true, id, topic = row.SourceTopic });
}).WithName("ReplayDlq").RequireAuthorization("Operator");

// ── Schema hydration ───────────────────────────────────────────────────────────
await app.Services.GetRequiredService<IEmbeddingService>().InitializeAsync();
await app.Services.GetRequiredService<SchemaRegistry>().LoadAsync();
await app.Services.GetRequiredService<IRecordStoreSchemaManager>().ApplySchemaAsync(Iverson.Api.Reconciliation.ReconciliationSchema.Table);
await app.Services.GetRequiredService<IRecordStoreSchemaManager>().ApplySchemaAsync(Iverson.Api.Reconciliation.DlqSchema.Table);

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
