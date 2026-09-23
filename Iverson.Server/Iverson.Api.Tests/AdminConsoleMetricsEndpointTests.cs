using System.Text.Json;
using FluentAssertions;
using Iverson.Api.Console;
using Iverson.Embeddings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iverson.Api.Tests;

/// <summary>
/// <c>GET /admin/console/metrics</c>'s handler against the store conditions the pipeline test
/// cannot stage without a real or faked Prometheus — the "deployed but unreachable" and
/// "Prometheus answered" states, and the value-mapping/serialization contract between them.
/// Driven at the handler directly, the same way <c>AdminConsoleDataVolumeEndpointTests</c> drives
/// <c>AdminConsoleEndpoints.GetDataVolumeAsync</c>: the authorization gate is already pinned by
/// <c>AdminConsoleMetricsPipelineTests</c>, so what is left to verify here is entirely inside this
/// handler and <see cref="PrometheusQueries"/>.
/// </summary>
public class AdminConsoleMetricsEndpointTests
{
    private static IOptions<PrometheusOptions> Options(string baseUrl) =>
        Microsoft.Extensions.Options.Options.Create(new PrometheusOptions { BaseUrl = baseUrl });

    private static IOptions<EmbeddingServiceOptions> EmbeddingOptions(string baseUrl = "http://ollama:11434") =>
        Microsoft.Extensions.Options.Options.Create(new EmbeddingServiceOptions { BaseUrl = baseUrl });

    /// <summary>
    /// Records every PromQL string it was asked to run, and answers from a lookup keyed by that
    /// exact string — so a wrong query string (a route filter dropped, a name mis-mangled) fails
    /// as a missing-key KeyNotFoundException rather than silently returning some other widget's
    /// canned value.
    /// </summary>
    private sealed class FakeQueryClient : IPrometheusQueryClient
    {
        private readonly Dictionary<string, double?> _answers;
        public List<string> QueriesReceived { get; } = [];

        public FakeQueryClient(Dictionary<string, double?> answers) => _answers = answers;

        public Task<double?> QueryInstantAsync(string promQl, CancellationToken ct)
        {
            QueriesReceived.Add(promQl);
            if (!_answers.TryGetValue(promQl, out var value))
                throw new KeyNotFoundException($"FakeQueryClient was not primed for: {promQl}");
            return Task.FromResult(value);
        }
    }

    private sealed class ThrowingQueryClient(Exception ex) : IPrometheusQueryClient
    {
        public Task<double?> QueryInstantAsync(string promQl, CancellationToken ct) => throw ex;
    }

    private sealed class NeverCalledQueryClient : IPrometheusQueryClient
    {
        public Task<double?> QueryInstantAsync(string promQl, CancellationToken ct) =>
            throw new InvalidOperationException(
                "QueryInstantAsync must not be called when Prometheus is not configured.");
    }

    // ── State 1: not deployed ────────────────────────────────────────────────

    [Fact]
    public async Task BaseUrlEmpty_Returns503WithNotDeployedReason_AndNeverCallsTheClient()
    {
        var result = await AdminConsoleMetricsEndpoint.GetMetricsAsync(
            Options(""), EmbeddingOptions(), new NeverCalledQueryClient(), CancellationToken.None);

        var json = result.Should().BeOfType<JsonHttpResult<PrometheusUnavailableResponse>>().Subject;
        json.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        json.Value!.Reason.Should().Be("notDeployed");
    }

    // ── State 2: deployed but unreachable ────────────────────────────────────

    [Fact]
    public async Task ClientThrowsPrometheusUnavailable_Returns503WithUnreachableReason()
    {
        var result = await AdminConsoleMetricsEndpoint.GetMetricsAsync(
            Options("http://prometheus:9090"),
            EmbeddingOptions(),
            new ThrowingQueryClient(new PrometheusUnavailableException("connection refused")),
            CancellationToken.None);

        var json = result.Should().BeOfType<JsonHttpResult<PrometheusUnavailableResponse>>().Subject;
        json.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        json.Value!.Reason.Should().Be("unreachable");
        json.Value.Error.Should().Contain("connection refused");
    }

    // ── State 3: Prometheus answered ─────────────────────────────────────────

    private static Dictionary<string, double?> AllNineAnswered() => new()
    {
        [PrometheusQueries.ReconciliationQueueDepth] = 3,
        [PrometheusQueries.DlqUnreplayedCount] = 0, // a REAL zero — must not be confused with "no sample"
        [PrometheusQueries.DocumentRerenderQueueDepth] = 7,
        [PrometheusQueries.ConsumerRetriesPerSecond] = 0.5,
        [PrometheusQueries.ConsumerDlqRoutedPerSecond] = 0.1,
        [PrometheusQueries.RpcRequestsPerSecond] = 42.0,
        [PrometheusQueries.RpcErrorPercentage] = 1.25,
        [PrometheusQueries.RpcP95Seconds] = 0.084,
        [PrometheusQueries.EmbeddingP95Seconds("ollama")] = 0.512,
    };

    [Fact]
    public async Task AllNineSeriesAnswered_MapsEachValueToItsWidget()
    {
        var fake = new FakeQueryClient(AllNineAnswered());

        var result = await AdminConsoleMetricsEndpoint.GetMetricsAsync(
            Options("http://prometheus:9090"), EmbeddingOptions(), fake, CancellationToken.None);

        var ok = result.Should().BeOfType<Ok<MetricsResponse>>().Subject;
        var body = ok.Value!;

        body.FanOutBacklog.ReconciliationQueueDepth.Should().Be(3);
        body.FanOutBacklog.DlqUnreplayedCount.Should().Be(0);
        body.FanOutBacklog.DocumentRerenderQueueDepth.Should().Be(7);
        body.ConsumerActivity.ConsumerRetriesPerSecond.Should().Be(0.5);
        body.ConsumerActivity.ConsumerDlqRoutedPerSecond.Should().Be(0.1);
        body.RpcHealth.RequestsPerSecond.Should().Be(42.0);
        body.RpcHealth.ErrorPercentage.Should().Be(1.25);
        body.RpcHealth.P95Seconds.Should().Be(0.084);
        body.EmbeddingLatency.P95Seconds.Should().Be(0.512);

        // Nine values, nine queries — no widget silently shares another's answer, and nothing
        // extra was asked.
        fake.QueriesReceived.Should().HaveCount(9);
        fake.QueriesReceived.Distinct().Should().HaveCount(9);
    }

    /// <summary>
    /// THE NULL-VS-ZERO ASSERTION. A series with no current sample (the empty-vector case
    /// <c>PrometheusQueryClient.QueryInstantAsync</c> maps to <c>null</c>) must reach the wire as
    /// JSON <c>null</c>, not as a coerced <c>0</c> — indistinguishable on the wire from the real
    /// zero <c>DlqUnreplayedCount</c> carries in the fixture above.
    /// </summary>
    [Fact]
    public async Task ASeriesWithNoCurrentSample_IsNullOnTheWire_DistinctFromARealZero()
    {
        var answers = AllNineAnswered();
        answers[PrometheusQueries.ReconciliationQueueDepth] = null; // no sample yet
        var fake = new FakeQueryClient(answers);

        var result = await AdminConsoleMetricsEndpoint.GetMetricsAsync(
            Options("http://prometheus:9090"), EmbeddingOptions(), fake, CancellationToken.None);

        var ok = result.Should().BeOfType<Ok<MetricsResponse>>().Subject;
        ok.Value!.FanOutBacklog.ReconciliationQueueDepth.Should().BeNull();
        ok.Value.FanOutBacklog.DlqUnreplayedCount.Should().Be(0); // the real zero survives unchanged

        // camelCase explicitly: this is the minimal-API pipeline's own web default
        // (ConfigureHttpJsonOptions), not System.Text.Json's PascalCase default that a bare
        // JsonSerializer.Serialize(...) call would otherwise fall back to.
        var json = JsonSerializer.Serialize(
            ok.Value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var doc = JsonDocument.Parse(json);
        var fanOut = doc.RootElement.GetProperty("fanOutBacklog");
        fanOut.GetProperty("reconciliationQueueDepth").ValueKind.Should().Be(JsonValueKind.Null);
        fanOut.GetProperty("dlqUnreplayedCount").ValueKind.Should().Be(JsonValueKind.Number);
    }

    /// <summary>
    /// The embedding-latency query is built from <c>EmbeddingServiceOptions.BaseUrl</c>'s HOST,
    /// not its full URL with scheme/port — pinned by giving it a URL with a non-default port and
    /// confirming only the bare host reaches the PromQL the fake actually received.
    /// </summary>
    [Fact]
    public async Task EmbeddingLatencyQuery_UsesTheConfiguredOllamaHost()
    {
        var answers = AllNineAnswered();
        answers.Remove(PrometheusQueries.EmbeddingP95Seconds("ollama"));
        answers[PrometheusQueries.EmbeddingP95Seconds("my-ollama-host")] = 0.3;
        var fake = new FakeQueryClient(answers);

        var result = await AdminConsoleMetricsEndpoint.GetMetricsAsync(
            Options("http://prometheus:9090"),
            EmbeddingOptions("http://my-ollama-host:11434"),
            fake,
            CancellationToken.None);

        var ok = result.Should().BeOfType<Ok<MetricsResponse>>().Subject;
        ok.Value!.EmbeddingLatency.P95Seconds.Should().Be(0.3);
        fake.QueriesReceived.Should().Contain(q => q.Contains("server_address=\"my-ollama-host\""));
    }
}
