using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Caching.Memory;
using Iverson.Api;
using Iverson.Api.Authorization;
using Iverson.Api.Console;
using Iverson.Api.Consumers;
using Iverson.Api.Grpc;
using Iverson.Api.RateLimiting;
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
using Microsoft.AspNetCore.RateLimiting;
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

// CSR round-10 #3: which peers may vouch for a client address through X-Forwarded-For.
var trustedProxies = TrustedProxyOptions.FromConfiguration(cfg);

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
builder.Services.AddSingleton<RateLimitInterceptor>();
builder.Services.AddGrpc(options =>
{
    options.Interceptors.Add<RateLimitInterceptor>();
    options.Interceptors.Add<ActingUserInterceptor>();
});

// CSR finding #7 (round 7): per-principal rate limit for the /v1/traces relay — 60/min per
// "sub" claim, sliding window. The gRPC entity API's own per-principal limit is enforced by
// RateLimitInterceptor (registered above); this policy covers the one HTTP (non-gRPC) endpoint.
builder.Services.AddRateLimiter(options =>
{
    // 429 Too Many Requests is the semantically correct rejection status for a rate limit
    // (503 means "I'm down", not "you're too fast"); the gRPC side of this same remediation
    // uses the equivalent ResourceExhausted status. RateLimiterOptions defaults to 503 —
    // override it explicitly rather than relying on that default.
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx => PostAuthPartition(ctx, trustedProxies));
    options.OnRejected = (ctx, _) =>
    {
        ctx.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>()
            .LogWarning("[RateLimit] Rejected {Path}", ctx.HttpContext.Request.Path.ToString().SanitizeForLog());
        return ValueTask.CompletedTask;
    };
    options.AddPolicy("traces", ctx =>
        RateLimitPartition.GetSlidingWindowLimiter(
            ctx.User.FindFirst("sub")?.Value ?? "anon",
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueLimit = 0
            }));
});

// ── CORS (admin console) ────────────────────────────────────────────────────────
// The admin console is served from the shared iverson.local-style hostname while this
// API answers on the dedicated admin-api hostname (Task 4) — the browser's fetch calls
// to /health, /admin/*, and /v1/traces are cross-origin, so they need an explicit CORS
// policy or the preflight OPTIONS (and the real request behind it) fails closed.
// AllowAnyOrigin is never acceptable: with a bearer token in play, it would let any page
// on the internet drive these endpoints from a visitor's browser. AllowCredentials is
// deliberately NOT set — this API authenticates via a bearer token in the Authorization
// header, not a cookie, so credentialed CORS buys nothing here.
const string AdminConsoleCorsPolicy = "AdminConsole";
var adminConsoleOrigin = cfg["AdminConsole:Origin"];
if (!string.IsNullOrEmpty(adminConsoleOrigin))
{
    builder.Services.AddCors(options => options.AddPolicy(AdminConsoleCorsPolicy, policy => policy
        .WithOrigins(adminConsoleOrigin)
        .WithHeaders("Authorization", "Content-Type")
        .AllowAnyMethod()
        // Without this, the server sets X-Trace-Id on every response (below) but the browser's
        // fetch Response.headers cannot see it cross-origin — only the CORS-safelisted response
        // headers are exposed to script by default, and X-Trace-Id is not one of them.
        .WithExposedHeaders("X-Trace-Id")));
}
// If adminConsoleOrigin is unset, no CORS policy is registered at all — cross-origin
// requests fail closed rather than silently falling back to a permissive policy.

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = cfg["Authentication:Authority"];
        options.TokenValidationParameters.ValidIssuers = new[]
        {
            cfg["Authentication:Authority"],
            cfg["Authentication:ExternalIssuer"]
        };
        // Authentik's global issuer mode stamps iss with the minting request's own scheme and host. Tokens
        // minted in-cluster on 9000 carry http://<authentik>:9000/, which matched only because JwtBearer
        // appends the metadata document's issuer; with metadata now on 8443 that issuer is the https one.
        if (cfg["Authentication:InternalIssuer"] is { Length: > 0 } internalIssuer)
            options.TokenValidationParameters.ValidIssuers = options.TokenValidationParameters.ValidIssuers.Append(internalIssuer).ToArray();
        if (cfg["Authentication:MetadataAddress"] is { Length: > 0 } metadataAddress)
            options.MetadataAddress = metadataAddress;
        options.TokenValidationParameters.ValidAudiences = cfg.GetSection("Authentication:ValidAudiences").Get<string[]>();
        // CSR round-10 #5: discovery and JWKS come from the tls-proxy sidecar over https, validated
        // against the deployment's own CA (AuthentikTrust).
        options.RequireHttpsMetadata = true;
        options.BackchannelHttpHandler = Iverson.Api.Tenancy.AuthentikTrust.CreateHandler(cfg["Authentik:CaCertificatePath"]);
        // Without this, ASP.NET Core's default claim-type mapping silently renames the "sub"
        // claim to ClaimTypes.NameIdentifier (a legacy WS-Federation-era remapping table that
        // JwtBearerOptions.MapInboundClaims applies by default) — ActingUserInterceptor's
        // FindFirst("sub") would then always return null. Confirmed: this repo's only other
        // direct-claim-read code (OperatorAuthorizationPolicy) reads "groups"/"scope", neither
        // of which is in that remapping table, which is why this was never hit before.
        options.MapInboundClaims = false;
        options.Events = new JwtBearerEvents { OnTokenValidated = RejectRevokedTokenAsync };
    })
    .AddJwtBearer("ActingUser", options =>
    {
        options.Authority = cfg["Authentication:ActingUser:Authority"];
        options.TokenValidationParameters.ValidAudiences = cfg.GetSection("Authentication:ActingUser:ValidAudiences").Get<string[]>();
        options.RequireHttpsMetadata = true;
        options.BackchannelHttpHandler = Iverson.Api.Tenancy.AuthentikTrust.CreateHandler(cfg["Authentik:CaCertificatePath"]);
        if (cfg["Authentication:ActingUser:MetadataAddress"] is { Length: > 0 } actingMetadataAddress)
            options.MetadataAddress = actingMetadataAddress;
        // See the default scheme: acting-user tokens minted on 9000 carry the internal issuer, and
        // tokens minted through the public host carry the external one (Authentik's global issuer mode
        // stamps the host's root).
        string[] actingIssuers = [.. new[] { cfg["Authentication:InternalIssuer"], cfg["Authentication:ExternalIssuer"] }
            .OfType<string>().Where(issuer => issuer.Length > 0)];
        if (actingIssuers.Length > 0)
            options.TokenValidationParameters.ValidIssuers = actingIssuers;
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
            },
            OnTokenValidated = RejectRevokedTokenAsync
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
    // CSR round-10 #13: /v1/traces accepts console-issued tokens only; see
    // ConsoleClientAuthorizationPolicy.
    options.AddPolicy("ConsoleClient", policy => policy.RequireAssertion(context =>
        ConsoleClientAuthorizationPolicy.IsSatisfiedBy(
            context.User.FindAll("aud").Select(c => c.Value),
            cfg["Authentication:ConsoleAudience"])));
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

// CSR round 11 §3: tenant provisioning runs under its own StarRocks login, and only in the worker's
// engagement consumer (AddEngagementStoreConsumer below). The worker needs that connection string
// whenever engagement is on. The api role never provisions, so it is never handed one, and its
// repository refuses provisioning outright.
var starRocksProvisionerConnectionString =
    workloadRole == "worker" ? cfg.GetConnectionString("StarRocksProvisioner") : null;
if (string.IsNullOrWhiteSpace(starRocksProvisionerConnectionString))
{
    if (workloadRole == "worker" && engagementStoreEnabledAtStartup)
        throw new InvalidOperationException(
            "ConnectionStrings:StarRocksProvisioner is required for the worker role when Engagement:Enabled is true and was not configured.");
    starRocksProvisionerConnectionString = null;
}

// CSR round 12 §7.1: every StarRocks data connection logs in as the tenant's own user, whose password is
// derived from this key. Both roles derive it: the api to read, the worker to provision, write and aggregate.
var starRocksTenantLoginKey = cfg["StarRocks:TenantKey"];
if (string.IsNullOrWhiteSpace(starRocksTenantLoginKey))
{
    if (engagementStoreEnabledAtStartup)
        throw new InvalidOperationException(
            "StarRocks:TenantKey is required when Engagement:Enabled is true and was not configured.");
    starRocksTenantLoginKey = null;
}

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
    },
    provisionerConnectionString: starRocksProvisionerConnectionString,
    tenantLoginKey: starRocksTenantLoginKey);

builder.Services.AddQdrant(
    cfg["Qdrant:Host"] ?? "localhost",
    int.Parse(cfg["Qdrant:Port"] ?? "6334"),
    cfg["Qdrant:ApiKey"],
    cfg["Qdrant:CertPath"]);

// Spec §5: MatchPattern's limits, read one value at a time like EngagementQueryLimitOptions above.
var patternLimits = new PatternQueryLimitOptions
{
    MaxPatternLength       = cfg.GetValue($"{PatternQueryLimitOptions.Section}:MaxPatternLength", PatternQueryLimitOptions.Default.MaxPatternLength),
    MaxProgramInstructions = cfg.GetValue($"{PatternQueryLimitOptions.Section}:MaxProgramInstructions", PatternQueryLimitOptions.Default.MaxProgramInstructions),
    MaxExpressionLength    = cfg.GetValue($"{PatternQueryLimitOptions.Section}:MaxExpressionLength", PatternQueryLimitOptions.Default.MaxExpressionLength),
    MaxDefines             = cfg.GetValue($"{PatternQueryLimitOptions.Section}:MaxDefines", PatternQueryLimitOptions.Default.MaxDefines),
    MaxMeasures            = cfg.GetValue($"{PatternQueryLimitOptions.Section}:MaxMeasures", PatternQueryLimitOptions.Default.MaxMeasures),
    MaxSubsets             = cfg.GetValue($"{PatternQueryLimitOptions.Section}:MaxSubsets", PatternQueryLimitOptions.Default.MaxSubsets),
    MaxSimilarityTerms     = cfg.GetValue($"{PatternQueryLimitOptions.Section}:MaxSimilarityTerms", PatternQueryLimitOptions.Default.MaxSimilarityTerms),
    MaxOutputRows          = cfg.GetValue($"{PatternQueryLimitOptions.Section}:MaxOutputRows", PatternQueryLimitOptions.Default.MaxOutputRows),
    MaxRowsScanned         = cfg.GetValue($"{PatternQueryLimitOptions.Section}:MaxRowsScanned", PatternQueryLimitOptions.Default.MaxRowsScanned),
    MaxPartitionRows       = cfg.GetValue($"{PatternQueryLimitOptions.Section}:MaxPartitionRows", PatternQueryLimitOptions.Default.MaxPartitionRows),
    MaxActiveThreads       = cfg.GetValue($"{PatternQueryLimitOptions.Section}:MaxActiveThreads", PatternQueryLimitOptions.Default.MaxActiveThreads),
    MaxSteps               = cfg.GetValue($"{PatternQueryLimitOptions.Section}:MaxSteps", PatternQueryLimitOptions.Default.MaxSteps),
    TimeoutSeconds         = cfg.GetValue($"{PatternQueryLimitOptions.Section}:TimeoutSeconds", PatternQueryLimitOptions.Default.TimeoutSeconds),
    BatchRows              = cfg.GetValue($"{PatternQueryLimitOptions.Section}:BatchRows", PatternQueryLimitOptions.Default.BatchRows),
};
patternLimits.Validate();                                     // fail at startup, not on every request
builder.Services.AddSingleton(patternLimits);

builder.Services.AddVectorRanking(cfg);
builder.Services.AddDecayOptions(cfg);
builder.Services.AddPopularitySignalOptions(cfg);

builder.Services.AddKafka(cfg, isTransient: TransientFailures.IsTransient);

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
// The admin-console JSON endpoints' two readers. Both take the acting user as a method
// parameter — the gRPC-only ActingUserInterceptor never runs for them, so they are handed
// HttpContext.User instead. Singleton to match every dependency they hold.
builder.Services.AddSingleton<Iverson.Api.Schema.SchemaCatalogReader>();
builder.Services.AddSingleton<Iverson.Api.Search.AggregateReader>();
builder.Services.AddSingleton<ISchemaRegistrationOrchestrator, SchemaRegistrationOrchestrator>();
// CSR round 12 §5: RegisterSchema's DDL and registry write run in the worker. The api queues them
// in IversonPrivilegedRequests and waits; the worker-only services below run them.
builder.Services.AddSingleton<IPrivilegedRequestRepository>(sp => new PrivilegedRequestRepository(
    Iverson.Api.PrivilegedRequests.PrivilegedRequestSchema.TableName,
    sp.GetRequiredService<IRecordStoreQueryExecutor>()));
builder.Services.AddSingleton(new Iverson.Api.PrivilegedRequests.PrivilegedRequestWaits());
builder.Services.AddSingleton<Iverson.Api.PrivilegedRequests.PrivilegedRequestClient>();
builder.Services.AddSingleton<ISchemaRegistrationExecutor, Iverson.Api.PrivilegedRequests.QueuedSchemaRegistrationExecutor>();
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
// CSR round 12 §7.3: which tenants have a StarRocks login. AddStarRocks hands it to EngagementRepository, whose
// readers consult it before connecting and whose provisioning (worker only) records into it.
builder.Services.AddSingleton<IProvisionedTenantDirectory>(sp =>
    new Iverson.Api.Tenancy.PostgresProvisionedTenantDirectory(sp.GetRequiredService<IRecordStoreQueryExecutor>()));
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<Iverson.Api.Tenancy.ITenantStatusCache, Iverson.Api.Tenancy.TenantStatusCache>();
// CSR round-10 #11: the subs of removed and demoted users, and the 30 s snapshot of them that
// the JwtBearer schemes check every token against.
builder.Services.AddSingleton<ITokenRevocationRepository>(sp =>
    new TokenRevocationRepository(sp.GetRequiredService<IRecordStoreQueryExecutor>()));
builder.Services.AddSingleton<Iverson.Api.Tenancy.ITokenRevocationCache>(sp =>
    new Iverson.Api.Tenancy.TokenRevocationCache(
        sp.GetRequiredService<ITokenRevocationRepository>(), TimeProvider.System));
builder.Services.AddSingleton<Iverson.Api.Reconciliation.ReconciliationService>();

// CSR round-10 #5: the admin token travels over TLS to the tls-proxy sidecar, trusting only the
// deployment's CA. Authentik:BaseUrl has no fallback: the startup guard requires it outside
// Development, and the Development test hosts register their own clients.
var authentikAdminClient = builder.Services.AddHttpClient(Iverson.Api.Tenancy.IdpAdminClient.HttpClientName, client =>
{
    if (cfg["Authentik:BaseUrl"] is { Length: > 0 } authentikBaseUrl)
        client.BaseAddress = new Uri(authentikBaseUrl);
    var adminToken = cfg["Authentik:AdminToken"];
    if (!string.IsNullOrEmpty(adminToken))
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);
}).ConfigurePrimaryHttpMessageHandler(() => Iverson.Api.Tenancy.AuthentikTrust.CreateHandler(cfg["Authentik:CaCertificatePath"]));
// CSR round-10 #4 (C2): on Helm there is no static Authentik credential. The API exchanges its own
// projected service-account token for a short-lived access token instead; the startup guard makes the
// two modes mutually exclusive. One token cache serves every handler the factory builds, so a handler
// rotation does not mint a new token.
if (cfg["Authentik:OrchestratorAssertionPath"] is { } orchestratorAssertionPath && !string.IsNullOrWhiteSpace(orchestratorAssertionPath))
{
    var orchestratorTokenCache = new Iverson.Api.Tenancy.AuthentikOrchestratorTokenCache();
    authentikAdminClient.AddHttpMessageHandler(() => new Iverson.Api.Tenancy.AuthentikOrchestratorTokenHandler(
        orchestratorAssertionPath, cfg["Authentik:OrchestratorClientId"]!, TimeProvider.System, orchestratorTokenCache));
}
builder.Services.AddSingleton<Iverson.Api.Tenancy.IIdpAdminClient, Iverson.Api.Tenancy.IdpAdminClient>();

builder.Services.AddHttpClient("JaegerOtlpHttp", client =>
{
    client.BaseAddress = new Uri(cfg["Jaeger:OtlpHttpUrl"] ?? "http://iverson-jaeger:4318");
});

// The admin console's metrics proxy (/admin/console/metrics). BaseUrl is empty by default
// (appsettings.json) and stays empty on any profile that never installs the prometheus
// subchart (values-laptop.yaml) — the named client below is still registered unconditionally,
// same as JaegerOtlpHttp above, but AdminConsoleMetricsEndpoint checks PrometheusOptions.BaseUrl
// BEFORE ever asking this client to make a call, so an unconfigured deployment reports
// "notDeployed" rather than attempting (and failing) a connection to an empty BaseAddress.
builder.Services.Configure<Iverson.Api.Console.PrometheusOptions>(
    cfg.GetSection(Iverson.Api.Console.PrometheusOptions.Section));
builder.Services.AddHttpClient(Iverson.Api.Console.PrometheusQueryClient.HttpClientName, (sp, client) =>
{
    var promOptions = sp.GetRequiredService<IOptions<Iverson.Api.Console.PrometheusOptions>>().Value;
    if (!string.IsNullOrEmpty(promOptions.BaseUrl))
        client.BaseAddress = new Uri(promOptions.BaseUrl);
    // Short: this endpoint is polled by every open console tab, and a hung Prometheus must not
    // hang all of them for the framework's 100s default.
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddSingleton<Iverson.Api.Console.IPrometheusQueryClient, Iverson.Api.Console.PrometheusQueryClient>();

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
    builder.Services.AddSingleton<Iverson.Api.Consumers.PopularitySignalUpdater>();
    builder.Services.AddHostedService<Iverson.Api.Consumers.PopularitySignalConsumer>();
    builder.Services.AddHostedService<Iverson.Api.Reconciliation.PopularitySignalReconciliationWorker>();
    builder.Services.AddHostedService<Iverson.Api.Reconciliation.DlqMonitorConsumer>();
    builder.Services.AddHostedService<Iverson.Api.Reconciliation.ReconciliationQueueWorker>();
    builder.Services.AddHostedService<Iverson.Api.Reconciliation.DlqBacklogGaugeWorker>();

    builder.Services.Configure<Iverson.Api.Reconciliation.DocumentRerenderOptions>(
        cfg.GetSection(Iverson.Api.Reconciliation.DocumentRerenderOptions.Section));
    builder.Services.AddHostedService<Iverson.Api.Reconciliation.DocumentRerenderQueueWorker>();

    // CSR round 12 §5.1: the worker runs the privileged requests the api queues.
    builder.Services.AddSingleton<Iverson.Api.PrivilegedRequests.SchemaRegistrationApplier>();
    builder.Services.AddSingleton<Iverson.Api.PrivilegedRequests.IPrivilegedRequestExecutor, Iverson.Api.PrivilegedRequests.PrivilegedRequestExecutor>();
    builder.Services.AddHostedService<Iverson.Api.PrivilegedRequests.PrivilegedRequestWorker>();
}

// Registered for both roles (not gated on workloadRole): api itself runs multiple replicas
// in every non-local environment (values.yaml, api.replicas: 2), so a second api replica has
// the identical SchemaRegistry cache-coherence gap worker has — RegisterSchema only updates
// the calling process's own in-memory copy.
builder.Services.AddHostedService<Iverson.Api.Schema.SchemaRefreshWorker>();

// ── Middleware ─────────────────────────────────────────────────────────────────
var app = builder.Build();
// CSR round-10 #5: outside Development the api role reaches Authentik only over TLS, trusting only
// the CA the deployment mounts.
Iverson.Api.Tenancy.AuthentikTrust.ValidateStartup(app.Configuration, workloadRole, app.Environment);
app.MapPrometheusScrapingEndpoint().AllowAnonymous().WithMetadata(new RequireListenerPort(8081));

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

var preAuthOptions = new RateLimiterOptions
{
    // 429 Too Many Requests is the semantically correct rejection status for a rate limit
    // (503 means "I'm down", not "you're too fast"); mirrors the post-auth limiter's choice.
    RejectionStatusCode = StatusCodes.Status429TooManyRequests
};
preAuthOptions.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx => PreAuthPartition(ctx, trustedProxies));
// The existing "traces" named policy (registered only on the DI-configured post-auth options
// below) must also exist on this instance — RateLimiterOptions' policy map is per-instance, and
// /v1/traces carries .RequireRateLimiting("traces"); without this, every request to that endpoint
// throws InvalidOperationException before ever reaching the endpoint. No-op here (not a clone of
// the real per-sub policy, which has no "sub" claim to key on this early): the real 60/min budget
// stays enforced by the unchanged post-auth limiter below.
preAuthOptions.AddPolicy("traces", _ => RateLimitPartition.GetNoLimiter<string>("unlimited"));

app.Use(ListenerPortGateAsync);
app.UseRateLimiter(preAuthOptions);
app.UseHttpsRedirection();

// Must run before UseAuthentication: a cross-origin preflight OPTIONS carries no
// Authorization header, so if it reached the FallbackPolicy's RequireAuthenticatedUser
// first, every preflight would be rejected before CORS ever got to answer it.
// (AdminConsoleCorsPipelineTests.ConfiguredOrigin_PreflightOptions_AdminDlq_AnsweredByCors
// NotFallbackPolicy pins this: moving UseCors below UseAuthentication/UseAuthorization
// makes that one test fail with 401, confirmed empirically before this comment was
// restored.)
if (!string.IsNullOrEmpty(adminConsoleOrigin))
    app.UseCors(AdminConsoleCorsPolicy);

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();   // existing DI-configured GlobalLimiter, unchanged
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
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" })).WithName("HealthLive").AllowAnonymous().WithMetadata(new RequireListenerPort(8081));

app.MapGet("/build", () =>
{
    var (composite, assemblies) = BuildIdentity.Compute();
    return Results.Ok(new { composite, assemblies });
}).WithName("BuildIdentity").AllowAnonymous().WithMetadata(new RequireListenerPort(8081));

app.MapGet("/health", async (
    IRecordStoreQueryExecutor db,
    IEngagementStoreHealthCheck sr,
    IVectorSchemaManager vector,
    IEventBrokerHealthCheck kafka,
    IOptions<EngagementStoreOptions> engagementOptions,
    SchemaRegistry registry,
    IRecordStoreSchemaManager storeRoles,
    IMemoryCache cache) =>
{
    if (cache.TryGetValue("health-composite", out IResult? cached))
        return cached!;

    // CSR finding #7: this endpoint is AllowAnonymous, reachable by anything that can reach the
    // port — so every check here must be passive. Postgres reads (never writes), StarRocks'
    // CheckHealthAsync is SELECT 1 + a backend-status read, Qdrant's PingAsync lists collections
    // (never creates one), and Kafka's PingAsync reads broker metadata (never produces). None of
    // the four performs a write; do not reintroduce one here.
    var pgTask     = db.QuerySingleOrDefaultAsync<int>("SELECT 1").ContinueWith(t => t.IsCompletedSuccessfully && t.Result == 1);
    // CSR round 12 §6.3. The worker grants the api's login its privileges at startup, so an api can
    // be up before they exist. Readiness waits for them; for the login's iverson_runtime membership,
    // probed with the same SET LOCAL ROLE the api's startup runs, in a transaction that is rolled
    // back; and for the registry's first load, which the api retries in the background instead of
    // crash-looping. A read, a rolled-back role switch and a flag, so the endpoint stays passive,
    // and all three sit behind the 2 s cache below. For the worker all three hold once its own
    // startup has run: it owns the tables and is a member of iverson_runtime.
    var grantsTask = db.QuerySingleOrDefaultAsync<bool>(ApiLoginGrants.ReadinessProbeSql).ContinueWith(t => t.IsCompletedSuccessfully && t.Result);
    var roleTask   = storeRoles.EnsureCanEnterRuntimeRoleAsync().ContinueWith(t => t.IsCompletedSuccessfully);
    var srTask     = sr.CheckHealthAsync();
    var vectorTask = vector.PingAsync();
    var kafkaTask  = kafka.PingAsync();

    await Task.WhenAll(pgTask, grantsTask, roleTask, srTask, vectorTask, kafkaTask);

    var srStatus = await srTask;
    var engagementEnabled = engagementOptions.Value.Enabled;
    var registryLoaded = registry.HasLoaded;
    var checks = new
    {
        postgres    = pgTask.Result,
        apiGrants   = grantsTask.Result,
        runtimeRole = roleTask.Result,
        registry    = registryLoaded,
        starrocks   = engagementEnabled ? (object)(srStatus == EngagementHealthStatus.Healthy) : "disabled",
        qdrant      = vectorTask.Result,
        kafka       = kafkaTask.Result
    };

    var readiness = ReadinessPolicy.Evaluate(
        pgTask.Result && grantsTask.Result && roleTask.Result && registryLoaded,
        srStatus, vectorTask.Result, kafkaTask.Result, engagementEnabled);

    var result = readiness.Ready
        ? Results.Ok(new { status = readiness.FullyHealthy ? "healthy" : "degraded", checks })
        : Results.Json(new { status = "degraded", checks }, statusCode: 503);

    cache.Set("health-composite", result, TimeSpan.FromSeconds(2));
    return result;
})
.WithName("Health")
.AllowAnonymous()
.WithMetadata(new RequireListenerPort(8081));

app.MapPost("/admin/reconcile/{typeName}", async (
    string typeName,
    Iverson.Api.PrivilegedRequests.PrivilegedRequestClient privileged,
    AuditLog audit,
    HttpContext httpContext) =>
{
    var actingUserResult = await httpContext.AuthenticateAsync("ActingUser");
    if (!actingUserResult.Succeeded || actingUserResult.Principal is null)
        return Results.Unauthorized();

    var isOperator = OperatorAuthorizationPolicy.IsSatisfiedBy(
        actingUserResult.Principal.FindAll("groups").Select(c => c.Value),
        actingUserResult.Principal.FindFirst("scope")?.Value);
    if (!isOperator)
        return Results.Forbid();

    // CSR round 12 §5.2: the reconcile reads every tenant's rows under the maintenance role, so it
    // runs in the worker. It is idempotent, so the request never expires: past the wait the worker
    // still completes it, and the caller gets 202 with the request id. The request's abort ends
    // only this wait, never the reconcile.
    return await RunPrivilegedAdminRequestAsync(
        privileged,
        Iverson.Api.PrivilegedRequests.PrivilegedRequestKinds.ReconcileType,
        JsonSerializer.Serialize(new Iverson.Api.PrivilegedRequests.ReconcileTypePayload(typeName), SchemaRegistry.JsonOptions),
        actingUserResult.Principal.FindFirst("tenant_id")?.Value,
        privileged.Waits.Reconcile,
        () => audit.AdminOperation(httpContext.User, "Reconcile", typeName),
        result => Results.Ok(new { reconciledCount = result.Count, typeName }),
        httpContext.RequestAborted);
}).WithName("Reconcile").RequireAuthorization("Operator").WithMetadata(new RequireListenerPort(8080));

app.MapGet("/admin/dlq", async (IDlqRepository dlq, Iverson.Api.Tenancy.ITenantStatusCache tenantStatusCache, AuditLog audit, HttpContext httpContext) =>
{
    var actingUserResult = await httpContext.AuthenticateAsync("ActingUser");
    if (!actingUserResult.Succeeded || actingUserResult.Principal is null)
        return Results.Unauthorized();

    var isOperator = OperatorAuthorizationPolicy.IsSatisfiedBy(
        actingUserResult.Principal.FindAll("groups").Select(c => c.Value),
        actingUserResult.Principal.FindFirst("scope")?.Value);
    var actingTenantId = actingUserResult.Principal.FindFirst("tenant_id")?.Value;

    // CSR round 11 #21: an acting user whose tenant is not active is refused, as on the gRPC
    // surfaces and the admin console. ActiveTenantEndpointFilter reads HttpContext.User, which here
    // is the service token, so the acting principal is checked inline. No tenant_id passes.
    // The refusal is audited as the filter audits it, naming the acting principal, whose tenant
    // is the one refused.
    if (!string.IsNullOrEmpty(actingTenantId)
        && await tenantStatusCache.GetStatusAsync(actingTenantId) is null or "suspended" or "deleted")
    {
        audit.Denied(actingUserResult.Principal, "Unauthorized",
            httpContext.GetEndpoint()?.DisplayName ?? "unknown", null, "TenantNotActive");
        return Results.Forbid();
    }

    // An acting user with NO tenant_id claim must not fall through as though they matched every
    // untenanted row: `r.TenantId == actingTenantId` is `null == null` (true) for every row whose
    // TenantId is also null, which would bypass the isOperator gate entirely for such a caller.
    if (string.IsNullOrEmpty(actingTenantId) && !isOperator)
        return Results.Forbid();

    var rows = await dlq.ListUnreplayedAsync(200);
    var visible = rows.Where(r => r.TenantId == actingTenantId || (r.TenantId is null && isOperator));
    audit.AdminOperation(httpContext.User, "ListDlq", null);
    return Results.Ok(visible);
}).WithName("ListDlq").RequireAuthorization("Operator").WithMetadata(new RequireListenerPort(8080));

app.MapPost("/admin/dlq/{id}/replay", async (Guid id, IDlqRepository dlq, Iverson.Api.PrivilegedRequests.PrivilegedRequestClient privileged, Iverson.Api.Tenancy.ITenantStatusCache tenantStatusCache, AuditLog audit, HttpContext httpContext) =>
{
    var actingUserResult = await httpContext.AuthenticateAsync("ActingUser");
    if (!actingUserResult.Succeeded || actingUserResult.Principal is null)
        return Results.Unauthorized();

    var isOperator = OperatorAuthorizationPolicy.IsSatisfiedBy(
        actingUserResult.Principal.FindAll("groups").Select(c => c.Value),
        actingUserResult.Principal.FindFirst("scope")?.Value);
    var actingTenantId = actingUserResult.Principal.FindFirst("tenant_id")?.Value;

    // Same inactive-tenant refusal and audit as /admin/dlq (see comment there), BEFORE the row fetch.
    if (!string.IsNullOrEmpty(actingTenantId)
        && await tenantStatusCache.GetStatusAsync(actingTenantId) is null or "suspended" or "deleted")
    {
        audit.Denied(actingUserResult.Principal, "Unauthorized",
            httpContext.GetEndpoint()?.DisplayName ?? "unknown", null, "TenantNotActive");
        return Results.Forbid();
    }

    // Same null-tenant_id-claim bypass as /admin/dlq (see comment there), checked BEFORE the row
    // fetch below: a caller who fails this first-level check must never learn — via 404 vs.
    // Forbid — whether the row id even exists.
    if (string.IsNullOrEmpty(actingTenantId) && !isOperator)
        return Results.Forbid();

    // The api's checks read the row's tenant and topic only, never MessageValue.
    var row = await dlq.GetUnreplayedSummaryAsync(id);
    if (row is null) return Results.NotFound(new { error = $"No unreplayed DLQ row with id '{id}'" });

    if (row.TenantId != actingTenantId && !(row.TenantId is null && isOperator))
        return Results.Forbid();

    // CSR round 12 §5.2: the worker produces the message and marks the row replayed, checking
    // Replayed=false again. The request never expires: a late replay of an unreplayed row is
    // still the replay that was asked for.
    return await RunPrivilegedAdminRequestAsync(
        privileged,
        Iverson.Api.PrivilegedRequests.PrivilegedRequestKinds.ReplayDlq,
        JsonSerializer.Serialize(new Iverson.Api.PrivilegedRequests.ReplayDlqPayload(id), SchemaRegistry.JsonOptions),
        actingTenantId,
        privileged.Waits.Replay,
        () => audit.AdminOperation(httpContext.User, "ReplayDlq", id.ToString()),
        _ => Results.Ok(new { replayed = true, id, topic = row.SourceTopic }),
        httpContext.RequestAborted);
}).WithName("ReplayDlq").RequireAuthorization("Operator").WithMetadata(new RequireListenerPort(8080));

// The admin console's four read-only JSON endpoints, under the same /admin prefix and — for
// three of the four — the same Operator policy as the routes above. /admin/console/schema and
// /admin/console/data-volume are authenticated-only on purpose and pass HttpContext.User to
// their readers; see AdminConsoleEndpoints for why neither may be normalised to Operator.
app.MapAdminConsoleEndpoints();

// The console's fifth endpoint, Operator-gated like /tenants and /qdrant above — a Prometheus
// proxy over a fixed, server-authored query set. In its own file/registration call per the SDD
// pre-flight ledger's Ruling 1; see AdminConsoleMetricsEndpoint for the three-state contract.
app.MapAdminConsoleMetricsEndpoint();

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
var schemaManager  = app.Services.GetRequiredService<IRecordStoreSchemaManager>();

if (workloadRole == "worker")
{
    // CSR round 12 §6.3. All startup DDL and the owner's startup writes run here, in the worker,
    // which connects as the database owner. WorkerStoreBootstrap creates the plumbing tables,
    // ensures the entity roles, and grants the api's login what it may do. On Helm that login is a
    // CNPG managed role that can appear shortly after the worker starts, so the worker waits up to
    // a minute for it. Compose has no CNPG, and its superuser worker creates the login from
    // Postgres:ApiLoginPassword.
    await WorkerStoreBootstrap.RunAsync(
        schemaManager,
        app.Services.GetRequiredService<ISchemaRegistryRepository>(),
        app.Services.GetRequiredService<IEnrichmentStateRepository>(),
        app.Services.GetRequiredService<IDocumentRerenderQueueRepository>(),
        app.Services.GetRequiredService<ITokenRevocationRepository>(),
        cfg["Postgres:ApiLoginPassword"],
        TimeSpan.FromMinutes(1));

    if (cfg.GetValue("Tenancy:SeedLegacyTenants", false))
    {
        var tenantRepository = app.Services.GetRequiredService<ITenantRepository>();
        foreach (var legacyTenantId in new[] { "tenant_loadtest", "tenant_webtest", "tenant_admin", "tenant_smoke_test", "tenant_bypass" })
            await tenantRepository.SeedIfMissingAsync(legacyTenantId, legacyTenantId, "active");
    }

    await schemaRegistry.LoadAsync();

    // Self-heal RLS state for tables whose descriptor was registered before this change shipped —
    // their physical DDL predates the tenant policy/RLS/grant this schema manager now applies.
    foreach (var descriptor in schemaRegistry.All.Values)
        await schemaManager.ApplySchemaAsync(SchemaBuilder.ToTableSchema(descriptor));
}
else
{
    // CSR round 12 §6.3. The api runs no DDL, and its role check is only the iverson_runtime probe.
    // On a fresh install it can start before the worker has created the tables or granted its
    // login, so a failure here is logged, not thrown. SchemaRefreshWorker retries the load every
    // 30 s, and /health reports not-ready until the registry has loaded and the login holds its
    // grants and can enter iverson_runtime (/health repeats this probe).
    try
    {
        await schemaManager.EnsureCanEnterRuntimeRoleAsync();
        await schemaRegistry.LoadAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex,
            "The api's Postgres access is not ready yet, most likely because the worker has not "
            + "finished its startup. The schema registry load retries in the background, and /health "
            + "reports not-ready until it succeeds.");
    }
}

PopularitySignalValidator.ValidateAtStartup(
    app.Services.GetRequiredService<IOptions<PopularitySignalOptions>>().Value,
    schemaRegistry,
    cfg.GetValue($"{EngagementStoreOptions.Section}:Enabled", true),
    app.Logger);

// ── gRPC endpoints ─────────────────────────────────────────────────────────────
if (workloadRole == "api")
{
    app.MapGrpcService<ObjectMappingGrpcService>().WithMetadata(new RequireListenerPort(8080));
    app.MapGrpcService<ObjectPersistenceGrpcService>().WithMetadata(new RequireListenerPort(8080));
    app.MapGrpcService<ObjectRetrievalGrpcService>().WithMetadata(new RequireListenerPort(8080));
    app.MapGrpcService<ObjectSearchGrpcService>().WithMetadata(new RequireListenerPort(8080));
    app.MapGrpcService<TenantLifecycleGrpcService>().RequireAuthorization("Operator").EnableGrpcWeb().WithMetadata(new RequireListenerPort(8080));
    app.MapGrpcService<TenantAdminGrpcService>().RequireAuthorization("TenantAdmin").EnableGrpcWeb().WithMetadata(new RequireListenerPort(8080));

    // Relays the admin-ui browser's OTel Web SDK spans to Jaeger's OTLP/HTTP endpoint.
    // Served by the API so the browser never needs Jaeger's own network address, and limited to
    // tokens the console's own OIDC client issued (the ConsoleClient policy, CSR round-10 #13),
    // so only signed-in admin-ui sessions can write traces through it — not service clients or
    // acting-user tokens.
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
    //
    // Deliberately NOT pinned to a listener, and accepting CORS preflights. The admin console
    // calls this cross-origin on its admin-api host, which the admin-api Ingress sends to the
    // Http1 listener (8081); 8080 is h2c-only, which no browser can speak. A MapPost endpoint
    // does not accept preflights, so without the HttpMethodMetadata below the browser's
    // OPTIONS /v1/traces would select gRPC's unimplemented-service catch-all, which carries
    // the gRPC services' RequireListenerPort(8080), and the listener gate would answer it 404
    // on 8081. Authentication, the "traces" per-user policy and both global limiters still
    // apply: the limiters exempt only endpoints pinned to 8081.
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
    }).RequireAuthorization("ConsoleClient")
        // CSR round 11 #21: the console token's tenant must be active; no tenant_id passes.
        .AddEndpointFilter<ActiveTenantEndpointFilter>()
        .RequireRateLimiting("traces")
        .WithMetadata(new HttpMethodMetadata(new[] { "POST" }, acceptCorsPreflight: true));
}

app.Lifetime.ApplicationStarted.Register(() =>
{
    app.Logger.LogInformation("Iverson.Api is available — all Kafka topic subscriptions initiated");
    app.Logger.LogInformation("OTel OTLP endpoint: {Endpoint}", otelEndpoint);
    app.Logger.LogInformation("Iverson.Api started in '{Role}' role", workloadRole);
});

app.Run();

// Extends the compiler-generated top-level-statements Program class. A bare method/local-
// function declaration can't carry an accessibility modifier and can't be called from
// another assembly — it must be a real member of a type for Step 6's tests to invoke it
// directly. This partial declaration must be `public`: merging an unmarked (implicitly
// internal) partial part with the compiler-synthesized entry-point class defeats the
// compiler's usual special-case suppression of CS0060 for that hidden class, so
// AuthTestWebApplicationFactory's `public sealed class ... : WebApplicationFactory<Program>`
// fails accessibility consistency checking (verified empirically) unless Program is public.
public partial class Program
{
    /// <summary>
    /// The privileged half of <c>/admin/reconcile</c> and <c>/admin/dlq/{id}/replay</c> (CSR round 12
    /// §5.2): queue the request with no expiry, wait up to <paramref name="wait"/> while it is pending
    /// or running, and answer what the worker recorded. 202 with the request id when it has recorded
    /// nothing yet, 404 or 500 with the worker's message when it failed, and <paramref name="ok"/>'s
    /// answer when it succeeded. The 202 and the success both write the audit entry.
    /// </summary>
    internal static async Task<IResult> RunPrivilegedAdminRequestAsync(
        Iverson.Api.PrivilegedRequests.PrivilegedRequestClient privileged,
        string kind,
        string payloadJson,
        string? requestedBy,
        TimeSpan wait,
        Action writeAuditEntry,
        Func<Iverson.Api.PrivilegedRequests.PrivilegedRequestResult, IResult> ok,
        CancellationToken ct)
    {
        var requestId = await privileged.SubmitAsync(kind, payloadJson, requestedBy, expiresAfter: null);
        var request = await privileged.WaitWhileAsync(
            requestId,
            status => status is PrivilegedRequestStatus.Pending or PrivilegedRequestStatus.Running,
            wait,
            ct);
        var result = Iverson.Api.PrivilegedRequests.PrivilegedRequestResult.From(request);

        if (result is null)
        {
            // The entry records the operator's request, not its outcome: the worker records that
            // later, in the privileged-request row.
            writeAuditEntry();
            return Results.Json(new { requestId }, statusCode: StatusCodes.Status202Accepted);
        }
        if (result.Code == global::Grpc.Core.StatusCode.NotFound)
            return Results.NotFound(new { error = result.Message });
        if (result.Code != global::Grpc.Core.StatusCode.OK)
            return Results.Json(new { error = result.Message }, statusCode: StatusCodes.Status500InternalServerError);

        writeAuditEntry();
        return ok(result);
    }

    /// <summary>The post-auth global limiter's partition (CSR round-7 #7, round-10 #3).</summary>
    internal static RateLimitPartition<string> PostAuthPartition(HttpContext ctx, TrustedProxyOptions trustedProxies)
    {
        // Exclude the gRPC data plane (already governed by RateLimitInterceptor's own 50,000/min
        // budget) and the anonymous probe/observability endpoints (kubelet, Prometheus — a 429
        // there pulls the pod out of service). Both are marked RequireListenerPort(8081) or are
        // gRPC methods, identified the same way Task 1's middleware identifies them, so this
        // list can never silently diverge from Task 1's own endpoint enumeration.
        var isHealthListenerEndpoint = ctx.GetEndpoint()?.Metadata.GetMetadata<RequireListenerPort>()?.Port == 8081;
        var isGrpcCall = ctx.Request.ContentType?.StartsWith("application/grpc") == true;
        if (isHealthListenerEndpoint || isGrpcCall)
            // A constant key, not the request path: PartitionedRateLimiter.Create caches one
            // dictionary entry per distinct partition key returned by this factory — including
            // no-op (GetNoLimiter) partitions, which are never swept. Keying by path let any
            // authenticated caller (every SDK client holds a service token) grow this dictionary
            // without bound by varying the path on gRPC-content-typed requests. The key carries
            // no meaning for a no-op partition, so collapse every excluded request onto one entry.
            return RateLimitPartition.GetNoLimiter("unlimited");

        return RateLimitPartition.GetSlidingWindowLimiter(
            ctx.User.FindFirst("sub")?.Value ?? ClientPartitionKey.For(ctx, trustedProxies),
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 6_000,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueLimit = 0
            });
    }

    /// <summary>The pre-auth global limiter's partition (CSR round-9 #2, round-10 #3).</summary>
    internal static RateLimitPartition<string> PreAuthPartition(HttpContext ctx, TrustedProxyOptions trustedProxies)
    {
        // Unlike the post-auth limiter, this one deliberately does NOT exclude gRPC calls:
        // gRPC requests with an invalid/expired token never reach RateLimitInterceptor (which
        // runs post-auth), so they have no rate-limit coverage outside this pre-auth limiter.
        // Excluding gRPC here would leave garbage-token gRPC calls with zero rate limiting,
        // reopening the vulnerability this pre-auth limiter exists to close.
        var isHealthListenerEndpoint = ctx.GetEndpoint()?.Metadata.GetMetadata<RequireListenerPort>()?.Port == 8081;
        if (isHealthListenerEndpoint)
            return RateLimitPartition.GetNoLimiter("unlimited");

        return RateLimitPartition.GetSlidingWindowLimiter(
            ClientPartitionKey.For(ctx, trustedProxies),
            _ => new SlidingWindowRateLimiterOptions
            {
                // Matches RateLimitInterceptor's 50,000/min-per-subject budget for authenticated gRPC
                // traffic: this limiter uniquely also covers gRPC pre-auth (see above), so it must not
                // sit below that dedicated budget or it becomes the accidental ceiling instead of it.
                PermitLimit = 50_000,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueLimit = 0
            });
    }

    /// <summary>
    /// CSR round-10 #11: both JwtBearer schemes' <c>OnTokenValidated</c>. Refuses a token whose
    /// <c>sub</c> was revoked (a removed user, or a demoted tenant admin) at or after its
    /// <c>iat</c>; a token with no <c>iat</c> cannot show it came later, so it is refused too.
    /// The default scheme then answers 401 / Unauthenticated, and the ActingUser scheme's callers
    /// treat the failed result as an invalid acting-user token. Service clients' subs are never
    /// revoked, so they always pass.
    /// </summary>
    internal static async Task RejectRevokedTokenAsync(TokenValidatedContext context)
    {
        var sub = context.Principal?.FindFirst("sub")?.Value;
        if (sub is null)
            return;

        DateTimeOffset? issuedAt = long.TryParse(context.Principal!.FindFirst("iat")?.Value, out var iat)
            ? DateTimeOffset.FromUnixTimeSeconds(iat)
            : null;

        var revocations = context.HttpContext.RequestServices.GetRequiredService<Iverson.Api.Tenancy.ITokenRevocationCache>();
        if (await revocations.IsRevokedAsync(sub, issuedAt, context.HttpContext.RequestAborted))
            context.Fail("Token has been revoked.");
    }

    internal sealed class RequireListenerPort(int port) { public int Port => port; }

    internal static Task ListenerPortGateAsync(HttpContext context, Func<Task> next)
    {
        var required = context.GetEndpoint()?.Metadata.GetMetadata<RequireListenerPort>();
        if (required is not null && context.Connection.LocalPort is not 0 &&
            context.Connection.LocalPort != required.Port)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }
        return next();
    }
}
