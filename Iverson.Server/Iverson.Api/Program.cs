using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Iverson.Api;
using Iverson.Api.Authorization;
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
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

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
builder.Services.AddGrpc(options => options.Interceptors.Add<ActingUserInterceptor>());

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = cfg["Authentication:Authority"];
        options.TokenValidationParameters.ValidIssuers = new[]
        {
            cfg["Authentication:Authority"],
            cfg["Authentication:ExternalIssuer"]
        };
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
    options.AddPolicy("SchemaAdmin", policy => policy.RequireAssertion(context =>
        SchemaAdminAuthorizationPolicy.IsSatisfiedBy(
            context.User.FindAll("groups").Select(c => c.Value),
            context.User.FindFirst("scope")?.Value)));
    options.AddPolicy("TenantAdmin", policy => policy.RequireAssertion(context =>
        TenantAdminAuthorizationPolicy.IsSatisfiedBy(
            context.User.FindAll("groups").Select(c => c.Value))));
});

builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, AuditingAuthorizationMiddlewareResultHandler>();

builder.Services.AddScoped<IActingUserAccessor, ActingUserAccessor>();

var engagementStoreEnabledAtStartup = cfg.GetValue($"{EngagementStoreOptions.Section}:Enabled", true);

// string.IsNullOrWhiteSpace, not `??`: an explicitly-configured "" (or whitespace-only string)
// is non-null and would otherwise silently bypass a plain `??` check, reaching AddPostgres /
// AddStarRocks without ever throwing even though the value is unusable as a real connection
// string. The plan's fail-closed intent is "no usable value", not merely "no null value".
var postgresConnectionString = cfg.GetConnectionString("Postgres");
builder.Services.AddPostgres(
    string.IsNullOrWhiteSpace(postgresConnectionString)
        ? throw new InvalidOperationException(
            "ConnectionStrings:Postgres is required and was not configured.")
        : postgresConnectionString);

var starRocksConnectionString = cfg.GetConnectionString("StarRocks");
builder.Services.AddStarRocks(
    string.IsNullOrWhiteSpace(starRocksConnectionString)
        ? (engagementStoreEnabledAtStartup
            ? throw new InvalidOperationException(
                "ConnectionStrings:StarRocks is required when Engagement:Enabled is true and was not configured.")
            : string.Empty)
        : starRocksConnectionString,
    new EngagementResilienceOptions
    {
        BackendReadyTimeout = TimeSpan.FromSeconds(cfg.GetValue("StarRocks:BackendReadyTimeoutSeconds", 120)),
        CircuitBreaker = new EngagementCircuitBreakerOptions
        {
            FailureRatio      = cfg.GetValue("StarRocks:CircuitBreaker:FailureRatio", 0.5),
            MinimumThroughput = cfg.GetValue("StarRocks:CircuitBreaker:MinimumThroughput", 4),
            SamplingDuration  = TimeSpan.FromSeconds(cfg.GetValue("StarRocks:CircuitBreaker:SamplingDurationSeconds", 30)),
            BreakDuration     = TimeSpan.FromSeconds(cfg.GetValue("StarRocks:CircuitBreaker:BreakDurationSeconds", 15))
        }
    },
    engagementStoreEnabledAtStartup,
    // CSR finding #5: caps on query-DSL shape (clause/join/GROUP BY key/pipeline step/window
    // function counts) so an authenticated tenant user cannot compose a request expensive enough
    // to degrade StarRocks for every tenant. Configurable under StarRocks:QueryLimits:*;
    // defaults to EngagementQueryLimitOptions' built-in values when unconfigured.
    // CSR finding #6 extends this same options object with OUTPUT-size caps (page size,
    // aggregation size, GROUP BY/pipeline limit, vector top_k, relation depth) alongside
    // round 2's shape caps above — same section, same configuration mechanism.
    new EngagementQueryLimitOptions
    {
        MaxClauses         = cfg.GetValue($"{EngagementQueryLimitOptions.Section}:MaxClauses", EngagementQueryLimitOptions.Default.MaxClauses),
        MaxJoins           = cfg.GetValue($"{EngagementQueryLimitOptions.Section}:MaxJoins", EngagementQueryLimitOptions.Default.MaxJoins),
        MaxGroupByKeys     = cfg.GetValue($"{EngagementQueryLimitOptions.Section}:MaxGroupByKeys", EngagementQueryLimitOptions.Default.MaxGroupByKeys),
        MaxPipelineSteps   = cfg.GetValue($"{EngagementQueryLimitOptions.Section}:MaxPipelineSteps", EngagementQueryLimitOptions.Default.MaxPipelineSteps),
        MaxWindowFunctions = cfg.GetValue($"{EngagementQueryLimitOptions.Section}:MaxWindowFunctions", EngagementQueryLimitOptions.Default.MaxWindowFunctions),
        MaxPageSize        = cfg.GetValue($"{EngagementQueryLimitOptions.Section}:MaxPageSize", EngagementQueryLimitOptions.Default.MaxPageSize),
        MaxAggregationSize = cfg.GetValue($"{EngagementQueryLimitOptions.Section}:MaxAggregationSize", EngagementQueryLimitOptions.Default.MaxAggregationSize),
        MaxGroupByLimit    = cfg.GetValue($"{EngagementQueryLimitOptions.Section}:MaxGroupByLimit", EngagementQueryLimitOptions.Default.MaxGroupByLimit),
        MaxTopK            = cfg.GetValue($"{EngagementQueryLimitOptions.Section}:MaxTopK", EngagementQueryLimitOptions.Default.MaxTopK),
        MaxRelationDepth   = cfg.GetValue($"{EngagementQueryLimitOptions.Section}:MaxRelationDepth", EngagementQueryLimitOptions.Default.MaxRelationDepth)
    });

builder.Services.AddQdrant(
    cfg["Qdrant:Host"] ?? "localhost",
    int.Parse(cfg["Qdrant:Port"] ?? "6334"),
    cfg["Qdrant:ApiKey"],
    cfg["Qdrant:CertPath"]);

builder.Services.AddVectorRanking(cfg);
builder.Services.AddDecayOptions(cfg);
builder.Services.AddPopularitySignalOptions(cfg);

builder.Services.AddKafka(cfg);

builder.Services.AddSingleton<SchemaRegistry>();
builder.Services.AddSingleton<DocumentRenderer>();
builder.Services.AddSingleton<IRelationValidator, RelationValidator>();
builder.Services.AddSingleton<IPayloadSizeValidator, PayloadSizeValidator>();
builder.Services.AddSingleton<IRowFieldAuthorizationEvaluator, RowFieldAuthorizationEvaluator>();
builder.Services.AddSingleton<IEntityKeyAccessor, EntityKeyAccessor>();
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton<IOutboxWriter>(sp => new OutboxWriter(
    Iverson.Api.Reconciliation.ReconciliationSchema.TableName,
    sp.GetRequiredService<IRecordStoreQueryExecutor>(),
    sp.GetRequiredService<IRecordStoreTransactionRunner>()));
builder.Services.AddSingleton<IOutboxPublisher, OutboxPublisher>();
builder.Services.AddSingleton<IEnrichmentStateRepository>(sp =>
    new EnrichmentStateRepository(sp.GetRequiredService<IRecordStoreQueryExecutor>()));
builder.Services.AddSingleton<IEntityRelationResolver, EntityRelationResolver>();
builder.Services.AddSingleton<ISchemaRegistrationOrchestrator, SchemaRegistrationOrchestrator>();
builder.Services.AddSingleton<IReconciliationQueueRepository>(sp => new ReconciliationQueueRepository(
    Iverson.Api.Reconciliation.ReconciliationSchema.TableName,
    sp.GetRequiredService<IRecordStoreQueryExecutor>()));
builder.Services.AddSingleton<IDocumentRerenderQueueRepository>(sp =>
    new DocumentRerenderQueueRepository(sp.GetRequiredService<IRecordStoreQueryExecutor>()));
builder.Services.AddSingleton<IDlqRepository>(sp => new DlqRepository(
    Iverson.Api.Reconciliation.DlqSchema.TableName,
    sp.GetRequiredService<IRecordStoreQueryExecutor>()));
builder.Services.AddSingleton<ITenantRepository>(sp => new TenantRepository(
    Iverson.Api.Tenancy.TenantSchema.TableName,
    sp.GetRequiredService<IRecordStoreQueryExecutor>()));
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<Iverson.Api.Tenancy.ITenantStatusCache, Iverson.Api.Tenancy.TenantStatusCache>();
builder.Services.AddSingleton<Iverson.Api.Reconciliation.ReconciliationService>();

// CSR finding #4: IdpAdminClient used to post a cleartext user password to Authentik's
// set_password endpoint over whatever transport this base URL specifies, which is what the
// startup guard formerly here existed to fail closed against for production/https profiles. The
// deeper fix (see IdpAdminClient.CreateUserAsync) removed the password transmission entirely —
// the platform never sends a user password to Authentik at all — so that guard's entire
// justification is gone. The remaining admin-token-over-plaintext-in-cluster hop is the accepted
// architecture decision this deployment already makes elsewhere, compensated by default-deny
// NetworkPolicy, not something this startup path needs to gate.
var authentikBaseUrlValue = cfg["Authentik:BaseUrl"] ?? "http://authentik-server:9000";

builder.Services.AddHttpClient(Iverson.Api.Tenancy.IdpAdminClient.HttpClientName, client =>
{
    client.BaseAddress = new Uri(authentikBaseUrlValue);
    var adminToken = cfg["Authentik:AdminToken"];
    if (!string.IsNullOrEmpty(adminToken))
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);
});
builder.Services.AddSingleton<Iverson.Api.Tenancy.IIdpAdminClient, Iverson.Api.Tenancy.IdpAdminClient>();

builder.Services.AddHttpClient("JaegerOtlpHttp", client =>
{
    client.BaseAddress = new Uri(cfg["Jaeger:OtlpHttpUrl"] ?? "http://iverson-jaeger:4318");
});

builder.Services.Configure<EngagementStoreOptions>(cfg.GetSection(EngagementStoreOptions.Section));

builder.Services.AddEmbeddings(cfg);

// AddEnrichment is unconditional (IEnrichmentService / IOptions<EnrichmentServiceOptions> are
// resolved outside the enrichment consumer too); only the hosted EnrichmentConsumer is gated on
// Enrichment__Enabled — see EnrichmentRegistration.
builder.Services.AddEnrichmentPipeline(cfg, workloadRole == "worker");

// Defense-in-depth: ConsumerResilience.RunWithRestartAsync already catches and retries
// every exception these hosted services can throw, but if something ever escapes that
// wrapper (e.g. a bug in the wrapper itself), StopHost's default behavior would take down
// the entire API. Ignore means only the faulted hosted service stops — not the whole process.
builder.Services.Configure<Microsoft.Extensions.Hosting.HostOptions>(o =>
    o.BackgroundServiceExceptionBehavior = Microsoft.Extensions.Hosting.BackgroundServiceExceptionBehavior.Ignore);

builder.Services.AddEngagementStoreConsumer(cfg, workloadRole == "worker");

if (workloadRole == "worker")
{
    builder.Services.AddHostedService<IntelligenceStoreConsumer>();
    builder.Services.AddHostedService<Iverson.Api.Consumers.DocumentRerenderConsumer>();
    builder.Services.AddSingleton<Iverson.Api.Consumers.PopularitySignalUpdater>();
    builder.Services.AddHostedService<Iverson.Api.Consumers.PopularitySignalConsumer>();
    builder.Services.AddHostedService<Iverson.Api.Reconciliation.PopularitySignalReconciliationWorker>();
    builder.Services.AddHostedService<Iverson.Api.Reconciliation.DlqMonitorConsumer>();
    builder.Services.AddHostedService<Iverson.Api.Reconciliation.ReconciliationQueueWorker>();
    builder.Services.AddHostedService<Iverson.Api.Reconciliation.DlqBacklogGaugeWorker>();

    builder.Services.Configure<Iverson.Api.Reconciliation.DocumentRerenderOptions>(
        cfg.GetSection(Iverson.Api.Reconciliation.DocumentRerenderOptions.Section));
    builder.Services.AddHostedService<Iverson.Api.Reconciliation.DocumentRerenderQueueWorker>();
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
app.UseGrpcWeb();

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

app.MapGet("/build", () =>
{
    var (composite, assemblies) = BuildIdentity.Compute();
    return Results.Ok(new { composite, assemblies });
}).WithName("BuildIdentity").AllowAnonymous();

app.MapGet("/health", async (
    IRecordStoreQueryExecutor db,
    IEngagementStoreHealthCheck sr,
    IVectorSchemaManager vector,
    IEventBrokerHealthCheck kafka,
    IOptions<EngagementStoreOptions> engagementOptions) =>
{
    // CSR finding #7: this endpoint is AllowAnonymous, reachable by anything that can reach the
    // port — so every check here must be passive. Postgres reads (never writes), StarRocks'
    // CheckHealthAsync is SELECT 1 + a backend-status read, Qdrant's PingAsync lists collections
    // (never creates one), and Kafka's PingAsync reads broker metadata (never produces). None of
    // the four performs a write; do not reintroduce one here.
    var pgTask     = db.QuerySingleOrDefaultAsync<int>("SELECT 1").ContinueWith(t => t.IsCompletedSuccessfully && t.Result == 1);
    var srTask     = sr.CheckHealthAsync();
    var vectorTask = vector.PingAsync();
    var kafkaTask  = kafka.PingAsync();

    await Task.WhenAll(pgTask, srTask, vectorTask, kafkaTask);

    var srStatus = await srTask;
    var engagementEnabled = engagementOptions.Value.Enabled;
    var checks = new
    {
        postgres  = pgTask.Result,
        starrocks = engagementEnabled ? (object)(srStatus == EngagementHealthStatus.Healthy) : "disabled",
        qdrant    = vectorTask.Result,
        kafka     = kafkaTask.Result
    };

    var readiness = ReadinessPolicy.Evaluate(
        pgTask.Result, srStatus, vectorTask.Result, kafkaTask.Result, engagementEnabled);

    return readiness.Ready
        ? Results.Ok(new { status = readiness.FullyHealthy ? "healthy" : "degraded", checks })
        : Results.Json(new { status = "degraded", checks }, statusCode: 503);
})
.WithName("Health")
.AllowAnonymous();

app.MapPost("/admin/reconcile/{typeName}", async (
    string typeName,
    Iverson.Api.Reconciliation.ReconciliationService reconciliation,
    AuditLog audit,
    HttpContext httpContext) =>
{
    var count = await reconciliation.ReconcileTypeAsync(typeName);
    if (count is null)
        return Results.NotFound(new { error = $"No schema registered for '{typeName}'" });

    audit.AdminOperation(httpContext.User, "Reconcile", typeName);
    return Results.Ok(new { reconciledCount = count, typeName });
}).WithName("Reconcile").RequireAuthorization("Operator");

app.MapGet("/admin/dlq", async (IDlqRepository dlq, AuditLog audit, HttpContext httpContext) =>
{
    var actingUserResult = await httpContext.AuthenticateAsync("ActingUser");
    if (!actingUserResult.Succeeded || actingUserResult.Principal is null)
        return Results.Unauthorized();

    var isOperator = OperatorAuthorizationPolicy.IsSatisfiedBy(
        actingUserResult.Principal.FindAll("groups").Select(c => c.Value),
        actingUserResult.Principal.FindFirst("scope")?.Value);
    var actingTenantId = actingUserResult.Principal.FindFirst("tenant_id")?.Value;

    // An acting user with NO tenant_id claim must not fall through as though they matched every
    // untenanted row: `r.TenantId == actingTenantId` is `null == null` (true) for every row whose
    // TenantId is also null, which would bypass the isOperator gate entirely for such a caller.
    if (string.IsNullOrEmpty(actingTenantId) && !isOperator)
        return Results.Forbid();

    var rows = await dlq.ListUnreplayedAsync(200);
    var visible = rows.Where(r => r.TenantId == actingTenantId || (r.TenantId is null && isOperator));
    audit.AdminOperation(httpContext.User, "ListDlq", null);
    return Results.Ok(visible);
}).WithName("ListDlq").RequireAuthorization("Operator");

app.MapPost("/admin/dlq/{id}/replay", async (Guid id, IDlqRepository dlq, IEventProducer events, AuditLog audit, HttpContext httpContext) =>
{
    var actingUserResult = await httpContext.AuthenticateAsync("ActingUser");
    if (!actingUserResult.Succeeded || actingUserResult.Principal is null)
        return Results.Unauthorized();

    var isOperator = OperatorAuthorizationPolicy.IsSatisfiedBy(
        actingUserResult.Principal.FindAll("groups").Select(c => c.Value),
        actingUserResult.Principal.FindFirst("scope")?.Value);
    var actingTenantId = actingUserResult.Principal.FindFirst("tenant_id")?.Value;

    // Same null-tenant_id-claim bypass as /admin/dlq (see comment there), checked BEFORE the row
    // fetch below: a caller who fails this first-level check must never learn — via 404 vs.
    // Forbid — whether the row id even exists.
    if (string.IsNullOrEmpty(actingTenantId) && !isOperator)
        return Results.Forbid();

    var row = await dlq.GetUnreplayedByIdAsync(id);
    if (row is null) return Results.NotFound(new { error = $"No unreplayed DLQ row with id '{id}'" });

    if (row.TenantId != actingTenantId && !(row.TenantId is null && isOperator))
        return Results.Forbid();

    await events.ProduceAsync(row.SourceTopic, row.MessageKey, row.MessageValue);
    await dlq.MarkReplayedAsync(id);
    audit.AdminOperation(httpContext.User, "ReplayDlq", id.ToString());

    return Results.Ok(new { replayed = true, id, topic = row.SourceTopic });
}).WithName("ReplayDlq").RequireAuthorization("Operator");

// ── Schema hydration ───────────────────────────────────────────────────────────
try
{
    await app.Services.GetRequiredService<IEmbeddingService>().EnsureInitializedAsync();
}
catch (Exception ex)
{
    // The embedding backend is commonly still downloading its model on a first install. Dying here
    // crash-loops both roles; CrashLoopBackOff then delays recovery by up to five minutes AFTER the
    // backend is healthy. Initialization retries lazily at the one place that needs the dimension
    // (schema registration), so continue.
    app.Logger.LogWarning(ex,
        "Embedding service not initialized at startup; will initialize on first schema registration.");
}
var schemaRegistry = app.Services.GetRequiredService<SchemaRegistry>();
await schemaRegistry.LoadAsync();

PopularitySignalValidator.ValidateAtStartup(
    app.Services.GetRequiredService<IOptions<PopularitySignalOptions>>().Value,
    schemaRegistry,
    cfg.GetValue($"{EngagementStoreOptions.Section}:Enabled", true),
    app.Logger);

// Plumbing table for the enrichment loop breaker — created the same way SchemaRegistry creates
// its own backing table (SchemaRegistry.LoadAsync → repository.EnsureTableAsync).
await app.Services.GetRequiredService<IEnrichmentStateRepository>().EnsureTableAsync();

// Plumbing table for the document re-render queue — same bootstrap shape as the enrichment
// loop breaker above; raw DDL because it needs the two partial unique indexes ApplySchemaAsync
// cannot express.
await app.Services.GetRequiredService<IDocumentRerenderQueueRepository>().EnsureTableAsync();

// EnsureRolesAsync must run before ANY ApplySchemaAsync call, since that DDL now GRANTs to
// iverson_maintenance on every table (and to iverson_runtime on the tenant-scoped ones) — both
// roles have to exist first.
var schemaManager = app.Services.GetRequiredService<IRecordStoreSchemaManager>();
await schemaManager.EnsureRolesAsync();
await schemaManager.ApplySchemaAsync(Iverson.Api.Reconciliation.ReconciliationSchema.Table);
await schemaManager.ApplySchemaAsync(Iverson.Api.Reconciliation.DlqSchema.Table);
await schemaManager.ApplySchemaAsync(Iverson.Api.Tenancy.TenantSchema.Table);

if (cfg.GetValue("Tenancy:SeedLegacyTenants", false))
{
    var tenantRepository = app.Services.GetRequiredService<ITenantRepository>();
    foreach (var legacyTenantId in new[] { "tenant_loadtest", "tenant_webtest", "tenant_admin", "tenant_smoke_test", "tenant_bypass" })
        await tenantRepository.SeedIfMissingAsync(legacyTenantId, legacyTenantId, "active");
}

// Self-heal RLS state for tables whose descriptor was registered before this change shipped —
// their physical DDL predates the tenant policy/RLS/grant this schema manager now applies.
foreach (var descriptor in schemaRegistry.All.Values)
    await schemaManager.ApplySchemaAsync(SchemaBuilder.ToTableSchema(descriptor));

// ── gRPC endpoints ─────────────────────────────────────────────────────────────
if (workloadRole == "api")
{
    app.MapGrpcService<ObjectMappingGrpcService>();
    app.MapGrpcService<ObjectPersistenceGrpcService>();
    app.MapGrpcService<ObjectRetrievalGrpcService>();
    app.MapGrpcService<ObjectSearchGrpcService>();
    app.MapGrpcService<TenantLifecycleGrpcService>().RequireAuthorization("Operator").EnableGrpcWeb();
    app.MapGrpcService<TenantAdminGrpcService>().RequireAuthorization("TenantAdmin").EnableGrpcWeb();

    // Relays the admin-ui browser's OTel Web SDK spans to Jaeger's OTLP/HTTP endpoint.
    // Same-origin so the browser never needs Jaeger's own network address, and
    // authenticated so only signed-in admin-ui sessions can write traces through it.
    // Body is relayed byte-for-byte (StreamContent straight from the request body), so this
    // must not attempt to parse or re-serialize it. The endpoint's only consumer is the
    // admin UI's browser OTel SDK, whose JsonTraceSerializer hardcodes
    // "Content-Type: application/json" — there is no -proto exporter dependency anywhere in
    // the admin UI — so JSON is the payload actually sent, not protobuf. Both
    // application/json and application/x-protobuf are allow-listed (the latter for any OTLP
    // exporter that does emit it); anything else is rejected with 415 rather than forwarded.
    // The body is also bounded, both by a declared-Content-Length check (so an oversized
    // request is rejected immediately, before any bytes are relayed to Jaeger) and by
    // IHttpMaxRequestBodySizeFeature (so a request that lies about its length is still cut
    // off by the transport once actually read) — this relay must not be usable to push an
    // unbounded payload at Jaeger.
    const long MaxTraceBodyBytes = 1 * 1024 * 1024; // 1 MiB: a browser span batch is KBs; ample headroom, still bounded.

    app.MapPost("/v1/traces", async (HttpContext ctx, IHttpClientFactory httpClientFactory) =>
    {
        var contentType = ctx.Request.ContentType;
        var mediaType = contentType is not null && MediaTypeHeaderValue.TryParse(contentType, out var parsedContentType)
            ? parsedContentType.MediaType
            : null;
        if (mediaType is not ("application/json" or "application/x-protobuf"))
        {
            ctx.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            return;
        }

        if (ctx.Request.ContentLength is long declaredLength && declaredLength > MaxTraceBodyBytes)
        {
            ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        var maxBodySizeFeature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (maxBodySizeFeature is not null && !maxBodySizeFeature.IsReadOnly)
            maxBodySizeFeature.MaxRequestBodySize = MaxTraceBodyBytes;

        var client = httpClientFactory.CreateClient("JaegerOtlpHttp");
        using var content = new StreamContent(ctx.Request.Body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType!);
        using var response = await client.PostAsync("/v1/traces", content);
        ctx.Response.StatusCode = (int)response.StatusCode;
        await response.Content.CopyToAsync(ctx.Response.Body);
    }).RequireAuthorization();
}

app.Lifetime.ApplicationStarted.Register(() =>
{
    app.Logger.LogInformation("Iverson.Api is available — all Kafka topic subscriptions initiated");
    app.Logger.LogInformation("OTel OTLP endpoint: {Endpoint}", otelEndpoint);
    app.Logger.LogInformation("Iverson.Api started in '{Role}' role", workloadRole);
});

app.Run();
