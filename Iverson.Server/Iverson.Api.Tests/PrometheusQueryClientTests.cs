using System.Net;
using System.Text;
using FluentAssertions;
using Iverson.Api.Console;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests;

/// <summary>
/// <see cref="PrometheusQueryClient"/> against fixture Prometheus HTTP API responses — the shape
/// documented at https://prometheus.io/docs/prometheus/latest/querying/api/#instant-queries.
/// Mirrors <c>AuthentikAdminClientTests</c>' <c>FakeHttpMessageHandler</c> pattern: a fake
/// <c>HttpMessageHandler</c> behind a real <see cref="HttpClient"/>, so what is pinned is the
/// client's actual request framing and response parsing, not a mock of its own dependency.
/// </summary>
public sealed class PrometheusQueryClientTests
{
    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class ThrowingHttpMessageHandler(Exception ex) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw ex;
    }

    private static PrometheusQueryClient CreateClient(HttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(PrometheusQueryClient.HttpClientName)
            .Returns(_ => new HttpClient(handler) { BaseAddress = new Uri("http://prometheus.local:9090") });
        return new PrometheusQueryClient(factory);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>
    /// Decodes the `query` parameter the client actually sent — the PromQL string under test.
    /// Manual parsing rather than a query-string-parsing helper library: the client's request URI
    /// always carries exactly one parameter (<c>PrometheusQueryClient.QueryInstantAsync</c> builds
    /// <c>?query=&lt;escaped&gt;</c> and nothing else), so stripping the fixed prefix and
    /// URL-decoding the remainder is unambiguous.
    /// </summary>
    private static string SentQuery(HttpRequestMessage request)
    {
        const string prefix = "?query=";
        var query = request.RequestUri!.Query;
        query.Should().StartWith(prefix);
        return Uri.UnescapeDataString(query[prefix.Length..]);
    }

    [Fact]
    public async Task SuccessWithOneResult_ReturnsTheParsedValue()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(HttpStatusCode.OK,
            """{"status":"success","data":{"resultType":"vector","result":[{"metric":{},"value":[1735689600,"3"]}]}}"""));
        var sut = CreateClient(handler);

        var value = await sut.QueryInstantAsync(PrometheusQueries.ReconciliationQueueDepth, CancellationToken.None);

        value.Should().Be(3);
        handler.Requests.Should().HaveCount(1);
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/api/v1/query");
        // THE MANGLED-NAME/ROUTE-FILTER ASSERTION for the gauge case: the exact PromQL string
        // sent over the wire, not merely what PrometheusQueries built in memory.
        SentQuery(handler.Requests[0]).Should().Be("max(reconciliation_queue_depth)");
    }

    [Fact]
    public async Task SuccessWithEmptyResultVector_ReturnsNull()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(HttpStatusCode.OK,
            """{"status":"success","data":{"resultType":"vector","result":[]}}"""));
        var sut = CreateClient(handler);

        var value = await sut.QueryInstantAsync(PrometheusQueries.DlqUnreplayedCount, CancellationToken.None);

        value.Should().BeNull();
    }

    /// <summary>
    /// A ratio query with no traffic in the window (RPC error percentage's 0/0) is NaN on
    /// Prometheus's wire, not an error — surfaced as "no current sample" rather than left as a
    /// non-finite double System.Text.Json would refuse to serialize.
    /// </summary>
    [Fact]
    public async Task SuccessWithNaNValue_ReturnsNull()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(HttpStatusCode.OK,
            """{"status":"success","data":{"resultType":"vector","result":[{"metric":{},"value":[1735689600,"NaN"]}]}}"""));
        var sut = CreateClient(handler);

        var value = await sut.QueryInstantAsync(PrometheusQueries.RpcErrorPercentage, CancellationToken.None);

        value.Should().BeNull();
    }

    [Fact]
    public async Task StatusError_ThrowsPrometheusUnavailableWithTheErrorMessage()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(HttpStatusCode.OK,
            """{"status":"error","errorType":"bad_data","error":"parse error at char 1"}"""));
        var sut = CreateClient(handler);

        var act = () => sut.QueryInstantAsync(PrometheusQueries.RpcP95Seconds, CancellationToken.None);

        (await act.Should().ThrowAsync<PrometheusUnavailableException>())
            .WithMessage("*parse error at char 1*");
    }

    [Fact]
    public async Task NonSuccessHttpStatus_ThrowsPrometheusUnavailable()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("upstream connection error")
            });
        var sut = CreateClient(handler);

        var act = () => sut.QueryInstantAsync(PrometheusQueries.RpcRequestsPerSecond, CancellationToken.None);

        (await act.Should().ThrowAsync<PrometheusUnavailableException>())
            .WithMessage("*503*upstream connection error*");
    }

    [Fact]
    public async Task MalformedJsonBody_ThrowsPrometheusUnavailable_NotAnUnhandledException()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            JsonResponse(HttpStatusCode.OK, "{not valid json"));
        var sut = CreateClient(handler);

        var act = () => sut.QueryInstantAsync(PrometheusQueries.RpcRequestsPerSecond, CancellationToken.None);

        await act.Should().ThrowAsync<PrometheusUnavailableException>();
    }

    [Fact]
    public async Task ConnectionFailure_ThrowsPrometheusUnavailable()
    {
        var handler = new ThrowingHttpMessageHandler(
            new HttpRequestException("Connection refused"));
        var sut = CreateClient(handler);

        var act = () => sut.QueryInstantAsync(PrometheusQueries.RpcRequestsPerSecond, CancellationToken.None);

        (await act.Should().ThrowAsync<PrometheusUnavailableException>())
            .WithMessage("*Connection refused*");
    }

    // ── The mangled series names and route filter actually sent over the wire ──

    [Theory]
    [InlineData("max(reconciliation_queue_depth)")]
    [InlineData("max(dlq_unreplayed_count)")]
    [InlineData("max(document_rerender_queue_depth)")]
    public async Task FanOutBacklogQueries_SendTheExactMangledSeriesName(string expectedQuery)
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(HttpStatusCode.OK,
            """{"status":"success","data":{"resultType":"vector","result":[]}}"""));
        var sut = CreateClient(handler);

        await sut.QueryInstantAsync(expectedQuery, CancellationToken.None);

        SentQuery(handler.Requests[0]).Should().Be(expectedQuery);
    }

    [Fact]
    public async Task RpcHealthQueries_ExcludeHealthAndScrapeRoutes_OnTheWire()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(HttpStatusCode.OK,
            """{"status":"success","data":{"resultType":"vector","result":[]}}"""));
        var sut = CreateClient(handler);

        await sut.QueryInstantAsync(PrometheusQueries.RpcRequestsPerSecond, CancellationToken.None);
        await sut.QueryInstantAsync(PrometheusQueries.RpcErrorPercentage, CancellationToken.None);
        await sut.QueryInstantAsync(PrometheusQueries.RpcP95Seconds, CancellationToken.None);

        foreach (var request in handler.Requests)
        {
            var sent = SentQuery(request);
            sent.Should().Contain("http_route!=\"/health\"");
            sent.Should().Contain("http_route!=\"/health/live\"");
            sent.Should().Contain("http_route!=\"/metrics\"");
            // The unit-suffixed real series name — not the bare OTel instrument name
            // ("http.server.request.duration") a naive dots-to-underscores mangle would produce.
            sent.Should().Contain("http_server_request_duration_seconds_");
        }
    }

    [Fact]
    public async Task EmbeddingLatencyQuery_UsesTheHttpClientSeries_WithTheSecondsUnitSuffix()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(HttpStatusCode.OK,
            """{"status":"success","data":{"resultType":"vector","result":[]}}"""));
        var sut = CreateClient(handler);

        await sut.QueryInstantAsync(PrometheusQueries.EmbeddingP95Seconds("ollama"), CancellationToken.None);

        var sent = SentQuery(handler.Requests[0]);
        sent.Should().Contain("http_client_request_duration_seconds_bucket");
        sent.Should().Contain("server_address=\"ollama\"");
    }
}
