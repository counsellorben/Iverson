using System.Globalization;
using System.Text.Json;
using Iverson.Embeddings;
using Microsoft.Extensions.Options;

namespace Iverson.Api.Console;

/// <summary>
/// <c>GET /admin/console/metrics</c> — a Prometheus proxy behind the console's fifth
/// <c>/admin/console/</c> JSON endpoint, in its own file per the SDD pre-flight ledger (Ruling 1:
/// this task carries its own <see cref="System.Net.Http.HttpClient"/> and config key, so keeping
/// it out of <c>AdminConsoleEndpoints.cs</c> keeps the two review surfaces distinct).
/// <para>
/// <b>Nine fixed named values, never a caller-supplied PromQL string.</b> This is the security
/// requirement the task carries, not a style preference: Prometheus scrapes every counter,
/// gauge and histogram this deployment emits, on one shared exporter (<c>Program.cs:71</c>,
/// <c>:275</c>). A pass-through query parameter on an <c>Operator</c>-gated endpoint would turn it
/// into an open query interface over all of that — including any series a future component adds
/// without anyone revisiting this file. Every PromQL string below is a compile-time constant or is
/// built from server-side configuration (the Ollama base URL), never from
/// <see cref="Microsoft.AspNetCore.Http.HttpContext"/>.
/// </para>
/// <para>
/// <b>Three states, distinguishable on the wire.</b> <c>values-laptop.yaml</c> sets
/// <c>prometheus.enabled: false</c> — a supported deployment shape, not a failure — so
/// "Prometheus is not deployed here" (503, <c>reason: "notDeployed"</c>, no HTTP call attempted),
/// "Prometheus is deployed but unreachable" (503, <c>reason: "unreachable"</c>) and "Prometheus
/// answered, and the value is zero" (200, a real <c>0</c>) are three different responses. A
/// single value that could mean any of the three is exactly the defect this plan's data-volume and
/// schema endpoints already had to fix twice (see <c>DataVolumeResponse</c> /
/// <c>SchemaCatalogResponse</c> in <c>AdminConsoleEndpoints.cs</c>) — this endpoint does not
/// reintroduce it. A fourth, per-value state — "Prometheus answered but this one series has no
/// samples yet" — is carried too, as a <c>null</c> on the individual metric rather than folded
/// into either 503 case or coerced to <c>0</c>: a fresh deployment before its first scrape
/// interval, or a counter that has genuinely never incremented, must not render as "zero backlog"
/// the same way a populated zero would.
/// </para>
/// <para>
/// <b>The RPC-health route filter lives in this endpoint's own PromQL, not on the shared metrics
/// provider.</b> <c>Program.cs:56-59</c> filters <c>/health</c> and <c>/health/live</c> on the
/// *tracing* provider only; <c>WithMetrics</c> (<c>:66-72</c>) adds
/// <c>AddAspNetCoreInstrumentation()</c> with no filter at all, so kubelet probes,
/// load-balancer health checks and Prometheus's own scrape of
/// <c>MapPrometheusScrapingEndpoint</c> (<c>:275</c>, route <c>/metrics</c>) are all counted as
/// RPC traffic. Excluding them here, in PromQL, rather than by mirroring the tracing filter onto
/// the metrics provider, is deliberate: that would change what the whole deployment exports for
/// every consumer of these metrics, permanently, not just what this one card displays.
/// </para>
/// </summary>
public static class AdminConsoleMetricsEndpoint
{
    public static IEndpointRouteBuilder MapAdminConsoleMetricsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet($"{AdminConsoleEndpoints.RoutePrefix}/metrics", GetMetricsAsync)
            .WithName("AdminConsoleMetrics")
            .RequireAuthorization("Operator");

        return app;
    }

    public static async Task<IResult> GetMetricsAsync(
        IOptions<PrometheusOptions> prometheusOptions,
        IOptions<EmbeddingServiceOptions> embeddingOptions,
        IPrometheusQueryClient prometheus,
        CancellationToken ct)
    {
        // State 1 of 3: not deployed. No HTTP call is even attempted — attempting one and
        // catching the inevitable failure would report this as "unreachable", which is a
        // different, and wrong, claim: nothing is misbehaving, this deployment profile simply
        // never installed the prometheus subchart (values-laptop.yaml:26).
        if (string.IsNullOrEmpty(prometheusOptions.Value.BaseUrl))
        {
            return Results.Json(
                new PrometheusUnavailableResponse(
                    "notDeployed", "Prometheus is not deployed in this environment."),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // HTTP client metrics label by server.address (the literal host), not by the logical
        // "embeddings" vs "enrichment" client name that registers them (Design 2's own caveat:
        // the two may conflate if both resolve to the same host) — so the filter is built from
        // the same BaseUrl the embeddings HttpClient itself is configured with, rather than a
        // hard-coded "ollama" hostname that configuration could silently drift away from.
        var ollamaHost = TryGetHost(embeddingOptions.Value.BaseUrl);

        try
        {
            var reconciliationTask = prometheus.QueryInstantAsync(PrometheusQueries.ReconciliationQueueDepth, ct);
            var dlqUnreplayedTask = prometheus.QueryInstantAsync(PrometheusQueries.DlqUnreplayedCount, ct);
            var documentRerenderTask = prometheus.QueryInstantAsync(PrometheusQueries.DocumentRerenderQueueDepth, ct);
            var consumerRetriesTask = prometheus.QueryInstantAsync(PrometheusQueries.ConsumerRetriesPerSecond, ct);
            var consumerDlqRoutedTask = prometheus.QueryInstantAsync(PrometheusQueries.ConsumerDlqRoutedPerSecond, ct);
            var rpcRequestRateTask = prometheus.QueryInstantAsync(PrometheusQueries.RpcRequestsPerSecond, ct);
            var rpcErrorPercentageTask = prometheus.QueryInstantAsync(PrometheusQueries.RpcErrorPercentage, ct);
            var rpcP95Task = prometheus.QueryInstantAsync(PrometheusQueries.RpcP95Seconds, ct);
            var embeddingP95Task = prometheus.QueryInstantAsync(PrometheusQueries.EmbeddingP95Seconds(ollamaHost), ct);

            await Task.WhenAll(
                reconciliationTask, dlqUnreplayedTask, documentRerenderTask,
                consumerRetriesTask, consumerDlqRoutedTask,
                rpcRequestRateTask, rpcErrorPercentageTask, rpcP95Task,
                embeddingP95Task);

            return Results.Ok(new MetricsResponse(
                new FanOutBacklogMetrics(
                    reconciliationTask.Result, dlqUnreplayedTask.Result, documentRerenderTask.Result),
                new ConsumerActivityMetrics(consumerRetriesTask.Result, consumerDlqRoutedTask.Result),
                new RpcHealthMetrics(rpcRequestRateTask.Result, rpcErrorPercentageTask.Result, rpcP95Task.Result),
                new EmbeddingLatencyMetrics(embeddingP95Task.Result)));
        }
        // State 2 of 3: deployed but unreachable — a distinct 503 reason from state 1, and never
        // a 500: this is a store-unavailability condition (Prometheus is "the store" for this
        // endpoint), the same translation AdminConsoleEndpoints.GetDataVolumeAsync applies to
        // EngagementNotReadyException / EngagementStoreDisabledException.
        catch (PrometheusUnavailableException ex)
        {
            return Results.Json(
                new PrometheusUnavailableResponse("unreachable", ex.Message),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static string TryGetHost(string baseUrl)
    {
        // Best-effort: a malformed EmbeddingServiceOptions.BaseUrl is a configuration problem
        // for the embeddings client itself to fail loudly on elsewhere. Here it would only make
        // the embedding-latency PromQL match nothing (an empty result, surfaced as a null on the
        // wire per the class doc's fourth state) rather than throw and take the other eight
        // values down with it.
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : baseUrl;
    }
}

/// <summary>
/// The Prometheus base URL, e.g. <c>http://iverson-prometheus:9090</c> in-cluster. Empty
/// (the default) means "not deployed" — <see cref="AdminConsoleMetricsEndpoint.GetMetricsAsync"/>
/// reports that state explicitly rather than attempting a doomed connection. Fed by
/// <c>Prometheus__BaseUrl</c> — see <c>charts/api/templates/deployment.yaml</c>, gated on
/// <c>.Values.global.prometheusEnabled</c> so the key is genuinely absent (not merely pointed at
/// a nonexistent Service) whenever the prometheus subchart is not installed.
/// </summary>
public sealed class PrometheusOptions
{
    public const string Section = "Prometheus";
    public string BaseUrl { get; set; } = "";
}

/// <summary>
/// A minimal Prometheus HTTP API client: one instant-query call, one number back (or
/// <c>null</c> if the series has no current sample). Nothing here accepts caller-supplied PromQL —
/// every query this endpoint issues is a compile-time constant or server-built from configuration;
/// see <see cref="PrometheusQueries"/>.
/// </summary>
public interface IPrometheusQueryClient
{
    /// <summary>
    /// Runs <paramref name="promQl"/> as a Prometheus instant query and returns the first result's
    /// value. <c>null</c> means Prometheus answered successfully but the series has no current
    /// sample (an empty result vector) — distinct from a real <c>0</c>, and distinct from
    /// <see cref="PrometheusUnavailableException"/>, which means Prometheus itself could not be
    /// reached or returned an error.
    /// </summary>
    Task<double?> QueryInstantAsync(string promQl, CancellationToken ct);
}

/// <summary>Prometheus could not be reached, timed out, or returned a non-success status/body.</summary>
public sealed class PrometheusUnavailableException : Exception
{
    public PrometheusUnavailableException(string message) : base(message) { }
    public PrometheusUnavailableException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Wraps Prometheus's HTTP API (<c>GET /api/v1/query</c>) — the instant-query form documented at
/// https://prometheus.io/docs/prometheus/latest/querying/api/#instant-queries. Follows the same
/// <c>IHttpClientFactory</c> + named-client convention as
/// <c>Iverson.Api.Tenancy.IdpAdminClient</c>.
/// </summary>
public sealed class PrometheusQueryClient(IHttpClientFactory httpClientFactory) : IPrometheusQueryClient
{
    public const string HttpClientName = "iverson.prometheus";

    public async Task<double?> QueryInstantAsync(string promQl, CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient(HttpClientName);

        // Disposed via `using` below regardless of which path this method takes out (a real
        // number, null for no current sample, or throwing PrometheusUnavailableException) — a
        // bare `HttpResponseMessage response;` here previously left it undisposed on every path,
        // and this is called nine times per poll for every open console tab.
        using var response = await SendAsync(client, promQl, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new PrometheusUnavailableException(
                $"Prometheus returned HTTP {(int)response.StatusCode}: {Truncate(body)}");
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
            if (status != "success")
            {
                var error = root.TryGetProperty("error", out var errorEl)
                    ? errorEl.GetString()
                    : "Prometheus reported an error with no message";
                throw new PrometheusUnavailableException($"Prometheus query failed: {error}");
            }

            var result = root.GetProperty("data").GetProperty("result");
            if (result.GetArrayLength() == 0)
                return null;

            // Instant-query vector result shape: [{"metric": {...}, "value": [<ts>, "<string>"]}].
            // The sample value is always JSON-encoded as a string, even though it is numeric.
            var valueText = result[0].GetProperty("value")[1].GetString()!;
            var value = double.Parse(valueText, CultureInfo.InvariantCulture);

            // A ratio query (RPC error percentage) divides two rate()s that can both be zero when
            // there is no traffic in the window, which Prometheus reports as NaN rather than 0/0
            // erroring out. Surfaced as "no current sample" rather than a JSON NaN literal, which
            // System.Text.Json rejects by default.
            return double.IsFinite(value) ? value : null;
        }
        // A malformed or unexpectedly-shaped body is exactly as much "Prometheus did not answer
        // usefully" as a connection failure — translated the same way, so it reaches the
        // endpoint as a 503/"unreachable" rather than an unhandled 500.
        catch (Exception ex) when (
            ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new PrometheusUnavailableException($"Prometheus returned an unusable body: {ex.Message}", ex);
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string promQl, CancellationToken ct)
    {
        try
        {
            return await client.GetAsync($"/api/v1/query?query={Uri.EscapeDataString(promQl)}", ct);
        }
        catch (HttpRequestException ex)
        {
            throw new PrometheusUnavailableException($"Could not reach Prometheus: {ex.Message}", ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // The caller's own token was not the source — this is the HttpClient's own request
            // timeout, which is a Prometheus-unavailability condition, not a cancelled request.
            throw new PrometheusUnavailableException("Prometheus request timed out.", ex);
        }
    }

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500] + "...";
}

/// <summary>
/// The nine PromQL strings this endpoint issues, and nothing else — see the class doc on
/// <see cref="AdminConsoleMetricsEndpoint"/> for why no tenth, caller-supplied string may ever
/// join this list.
/// <para>
/// Names are Prometheus-mangled instrument names: dots to underscores, <c>_total</c> on counters,
/// <c>_bucket</c>/<c>_sum</c>/<c>_count</c> on histograms, and the instrument's declared unit
/// appended where it has one. The five gauges/counters below are declared with no unit
/// (<c>ReconciliationTelemetry.cs</c>, <c>Iverson.Events/Telemetry.cs</c>) and mangle straight;
/// the two duration histograms are declared in seconds by
/// <c>OpenTelemetry.Instrumentation.AspNetCore</c> / <c>.Http</c>, so their real series carry the
/// <c>_seconds</c> segment — <c>http_server_request_duration_seconds_*</c> and
/// <c>http_client_request_duration_seconds_*</c>, not the un-suffixed names a naive mangle of the
/// OTel instrument name (<c>http.server.request.duration</c>) would produce.
/// </para>
/// </summary>
internal static class PrometheusQueries
{
    /// <summary>
    /// All five instruments here — these three gauges and the two counters below — are emitted
    /// only by hosted services gated on `workloadRole == "worker"` (Program.cs), not by `api`.
    /// Multiple `worker` replicas (worker.replicas defaults to 2 — charts/worker/values.yaml) can
    /// each expose these gauges; every replica reads the same shared Postgres-backed queue, so the
    /// value is the same on every series and `max()` picks one without inventing a total `sum()`
    /// would. Restated per instrument: `sum()` IS correct for the two counters below, despite the
    /// same multi-replica shape, because those genuinely add across replicas — each consumer
    /// instance's own retries are its own, real, additional retries, not a repeated read of one
    /// shared number.
    /// </summary>
    internal const string ReconciliationQueueDepth = "max(reconciliation_queue_depth)";
    internal const string DlqUnreplayedCount = "max(dlq_unreplayed_count)";
    internal const string DocumentRerenderQueueDepth = "max(document_rerender_queue_depth)";

    /// <summary>Five-minute rolling rate — long enough to smooth a single scrape gap, short enough to read as "current".</summary>
    private const string RateWindow = "[5m]";

    internal const string ConsumerRetriesPerSecond = $"sum(rate(consumer_retries_total{RateWindow}))";
    internal const string ConsumerDlqRoutedPerSecond = $"sum(rate(consumer_dlq_routed_total{RateWindow}))";

    /// <summary>
    /// Excludes the two anonymous liveness/readiness routes and the Prometheus scrape endpoint's
    /// own route from every RPC-health query — see the class doc on
    /// <see cref="AdminConsoleMetricsEndpoint"/> for why this lives here rather than on the shared
    /// metrics provider. Route label values are literal path templates
    /// (<c>http_route="/health"</c>, not a regex), confirmed against the live provider's own
    /// `/metrics` output in the design spec's assumption A36.
    /// </summary>
    // Plain escaped literals throughout this class, deliberately, rather than raw string
    // literals: every value here embeds literal double-quote characters (PromQL label-matcher
    // syntax) immediately adjacent to points where a raw string's own triple-quote delimiter
    // would otherwise have to be reasoned about carefully. `\"` has one unambiguous meaning.
    private const string RpcRouteFilter =
        "http_route!=\"/health\",http_route!=\"/health/live\",http_route!=\"/metrics\"";

    internal const string RpcRequestsPerSecond =
        "sum(rate(http_server_request_duration_seconds_count{" + RpcRouteFilter + "}" + RateWindow + "))";

    // The numerator is wrapped in `or vector(0)` because an absent 5xx series (the common healthy
    // case — zero 5xx responses in the window) makes rate()/sum() evaluate to an empty vector,
    // and empty / scalar is empty, so the whole expression would read as "No data" rather than
    // "0%" on a healthy deployment. `vector(0)` is label-free, so it still matches the
    // denominator for the division. A genuinely empty denominator (no traffic at all) is left
    // alone, so that case still yields an honest null rather than a fabricated 0%.
    internal const string RpcErrorPercentage =
        "((sum(rate(http_server_request_duration_seconds_count{" + RpcRouteFilter
        + ",http_response_status_code=~\"5..\"}" + RateWindow + ")) or vector(0))"
        + " / sum(rate(http_server_request_duration_seconds_count{" + RpcRouteFilter + "}" + RateWindow
        + "))) * 100";

    internal const string RpcP95Seconds =
        "histogram_quantile(0.95, sum(rate(http_server_request_duration_seconds_bucket{"
        + RpcRouteFilter + "}" + RateWindow + ")) by (le))";

    /// <summary>
    /// Filters on `server_address` — the OTel HTTP-client semantic-convention label for the
    /// literal request host — built from <c>EmbeddingServiceOptions.BaseUrl</c> rather than a
    /// hard-coded "ollama" hostname, since that base URL is configuration-driven
    /// (docker-compose vs. the Helm chart's in-cluster Service name).
    /// </summary>
    internal static string EmbeddingP95Seconds(string ollamaHost) =>
        "histogram_quantile(0.95, sum(rate(http_client_request_duration_seconds_bucket{server_address=\""
        + EscapeLabelValue(ollamaHost) + "\"}" + RateWindow + ")) by (le))";

    private static string EscapeLabelValue(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

// ── Response contracts ─────────────────────────────────────────────────────────

/// <summary>Reconciliation outbox, DLQ, and document re-render queue depths. <c>null</c> = no current sample.</summary>
public sealed record FanOutBacklogMetrics(
    double? ReconciliationQueueDepth, double? DlqUnreplayedCount, double? DocumentRerenderQueueDepth);

/// <summary>Consumer retry and DLQ-routing rate, per second, over the query window. <c>null</c> = no current sample.</summary>
public sealed record ConsumerActivityMetrics(double? ConsumerRetriesPerSecond, double? ConsumerDlqRoutedPerSecond);

/// <summary>
/// HTTP-transport health for this API's own request pipeline — not gRPC status semantics; a gRPC
/// call that returns a non-OK status inside an HTTP 200 does not count as an error here.
/// <c>null</c> = no current sample.
/// </summary>
public sealed record RpcHealthMetrics(double? RequestsPerSecond, double? ErrorPercentage, double? P95Seconds);

/// <summary>p95 latency of this API's outbound HTTP calls to Ollama. <c>null</c> = no current sample.</summary>
public sealed record EmbeddingLatencyMetrics(double? P95Seconds);

public sealed record MetricsResponse(
    FanOutBacklogMetrics FanOutBacklog,
    ConsumerActivityMetrics ConsumerActivity,
    RpcHealthMetrics RpcHealth,
    EmbeddingLatencyMetrics EmbeddingLatency);

/// <summary>Why Prometheus could not answer. <c>Reason</c> is <c>notDeployed</c> or <c>unreachable</c>.</summary>
public sealed record PrometheusUnavailableResponse(string Reason, string Error);
