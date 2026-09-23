using System.Text.RegularExpressions;
using FluentAssertions;
using Iverson.Api.Console;
using Xunit;

namespace Iverson.Api.Tests;

/// <summary>
/// Pins the Prometheus-mangled series names <see cref="PrometheusQueries"/> emits against the
/// instrument declarations themselves, not against a second copy of the same string constants —
/// a test that only checks the endpoint's PromQL against its own literals would be tautological
/// (Task 7 brief, Step 6).
/// <para>
/// The five gauge/counter names are read straight out of
/// <c>Iverson.Api/Reconciliation/ReconciliationTelemetry.cs</c> and
/// <c>Iverson.Events/Telemetry.cs</c> — the actual <c>CreateObservableGauge</c>/
/// <c>CreateCounter</c> call sites — via regex, the same "read the source of truth as text"
/// technique <c>RequirementsCoverageGateTests</c> uses. The two duration-histogram series cannot
/// be pinned the same way: <c>http.server.request.duration</c> /
/// <c>http.client.request.duration</c> and their declared "seconds" unit are internal to the
/// <c>OpenTelemetry.Instrumentation.AspNetCore</c> / <c>.Http</c> NuGet packages, not this repo's
/// own source. They are instead pinned against
/// <c>docs/specs/2026-08-25-admin-console-landing-page-design.md</c> — the design this task
/// implements against, which states the real series names explicitly (Design 1's "written out
/// here because the general rule is exactly where the `_seconds` segment gets dropped") after
/// having verified them against a live provider's own `/metrics` output (assumption A14/A36).
/// </para>
/// </summary>
public class AdminConsoleMetricsSeriesNamesTests
{
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Iverson.slnx")))
            dir = dir.Parent;

        if (dir is null)
            throw new InvalidOperationException(
                $"Could not locate repository root (a directory containing Iverson.slnx) by walking up from {AppContext.BaseDirectory}.");

        return dir.FullName;
    }

    private static string ReconciliationTelemetrySource() => File.ReadAllText(Path.Combine(
        RepositoryRoot(), "Iverson.Server", "Iverson.Api", "Reconciliation", "ReconciliationTelemetry.cs"));

    private static string EventsTelemetrySource() => File.ReadAllText(Path.Combine(
        RepositoryRoot(), "Iverson.Server", "Iverson.Events", "Telemetry.cs"));

    private static string DesignSpec() => File.ReadAllText(Path.Combine(
        RepositoryRoot(), "docs", "specs", "2026-08-25-admin-console-landing-page-design.md"));

    /// <summary>The literal instrument name in a <c>Meter.CreateObservableGauge("name", ...</c> call.</summary>
    private static string ExtractGaugeName(string source, string variablePrefix)
    {
        var match = Regex.Match(
            source,
            $@"CreateObservableGauge\(\s*""([^""]+)"",\s*\(\)\s*=>\s*{Regex.Escape(variablePrefix)}");
        match.Success.Should().BeTrue(
            $"expected to find a CreateObservableGauge(\"...\", () => {variablePrefix} call in the source");
        return match.Groups[1].Value;
    }

    /// <summary>The literal instrument name in a <c>Meter.CreateCounter&lt;long&gt;("name", ...</c> call bound to <paramref name="fieldName"/>.</summary>
    private static string ExtractCounterName(string source, string fieldName)
    {
        var match = Regex.Match(
            source,
            $@"{Regex.Escape(fieldName)}\s*=\s*Meter\.CreateCounter<long>\(\s*""([^""]+)""");
        match.Success.Should().BeTrue(
            $"expected to find `{fieldName} = Meter.CreateCounter<long>(\"...\"` in the source");
        return match.Groups[1].Value;
    }

    /// <summary>Dots to underscores; `_total` appended for a counter, nothing for a gauge — the rule Design 1 states.</summary>
    private static string Mangle(string dottedInstrumentName, bool isCounter) =>
        dottedInstrumentName.Replace('.', '_') + (isCounter ? "_total" : "");

    [Fact]
    public void ReconciliationQueueDepth_MatchesTheGaugeDeclaredInReconciliationTelemetry()
    {
        var instrumentName = ExtractGaugeName(ReconciliationTelemetrySource(), "ReconciliationQueueDepth");
        instrumentName.Should().Be("reconciliation.queue_depth"); // sanity: this is what the mangle below depends on

        PrometheusQueries.ReconciliationQueueDepth.Should().Contain(Mangle(instrumentName, isCounter: false));
    }

    [Fact]
    public void DlqUnreplayedCount_MatchesTheGaugeDeclaredInReconciliationTelemetry()
    {
        var instrumentName = ExtractGaugeName(ReconciliationTelemetrySource(), "DlqUnreplayedCount");
        instrumentName.Should().Be("dlq.unreplayed_count");

        PrometheusQueries.DlqUnreplayedCount.Should().Contain(Mangle(instrumentName, isCounter: false));
    }

    [Fact]
    public void DocumentRerenderQueueDepth_MatchesTheGaugeDeclaredInReconciliationTelemetry()
    {
        var instrumentName = ExtractGaugeName(ReconciliationTelemetrySource(), "DocumentRerenderQueueDepth");
        instrumentName.Should().Be("document_rerender.queue_depth");

        PrometheusQueries.DocumentRerenderQueueDepth.Should().Contain(Mangle(instrumentName, isCounter: false));
    }

    [Fact]
    public void ConsumerRetriesPerSecond_MatchesTheCounterDeclaredInEventsTelemetry()
    {
        var instrumentName = ExtractCounterName(EventsTelemetrySource(), "ConsumerRetries");
        instrumentName.Should().Be("consumer.retries");

        PrometheusQueries.ConsumerRetriesPerSecond.Should().Contain(Mangle(instrumentName, isCounter: true));
    }

    [Fact]
    public void ConsumerDlqRoutedPerSecond_MatchesTheCounterDeclaredInEventsTelemetry()
    {
        var instrumentName = ExtractCounterName(EventsTelemetrySource(), "ConsumerDlqRouted");
        instrumentName.Should().Be("consumer.dlq_routed");

        PrometheusQueries.ConsumerDlqRoutedPerSecond.Should().Contain(Mangle(instrumentName, isCounter: true));
    }

    /// <summary>
    /// The unit-suffixed real series name for the ASP.NET Core server-duration histogram — the
    /// case Design 1 calls out by name as "exactly where the `_seconds` segment gets dropped" by
    /// a naive mangle. Pinned against the design spec (see class doc for why the OTel package's
    /// own instrument declaration is not available as repo source to regex against), and asserted
    /// on all three histogram-suffixed forms this endpoint actually queries.
    /// </summary>
    [Fact]
    public void RpcHealthQueries_UseTheUnitSuffixedSeriesName_PerTheDesignSpec()
    {
        var spec = DesignSpec();
        spec.Should().Contain("http_server_request_duration_seconds_{bucket,sum,count}");

        PrometheusQueries.RpcRequestsPerSecond.Should().Contain("http_server_request_duration_seconds_count");
        PrometheusQueries.RpcErrorPercentage.Should().Contain("http_server_request_duration_seconds_count");
        PrometheusQueries.RpcP95Seconds.Should().Contain("http_server_request_duration_seconds_bucket");

        // The naive mangle of the OTel instrument name (http.server.request.duration) must NOT
        // appear anywhere in these three queries — the un-suffixed series does not exist.
        PrometheusQueries.RpcRequestsPerSecond.Should().NotContain("http_server_request_duration_count");
        PrometheusQueries.RpcErrorPercentage.Should().NotContain("http_server_request_duration_count");
        PrometheusQueries.RpcP95Seconds.Should().NotContain("http_server_request_duration_bucket");
    }

    [Fact]
    public void EmbeddingLatencyQuery_UsesTheUnitSuffixedHttpClientSeriesName_PerTheDesignSpec()
    {
        var spec = DesignSpec();
        spec.Should().Contain("http_client_request_duration_seconds_{bucket,sum,count}");

        var query = PrometheusQueries.EmbeddingP95Seconds("ollama");
        query.Should().Contain("http_client_request_duration_seconds_bucket");
        query.Should().NotContain("http_client_request_duration_bucket");
    }

    // ── Route exclusions ────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(RpcHealthQueries))]
    public void EveryRpcHealthQuery_ExcludesHealthAndScrapeRoutes(string query)
    {
        query.Should().Contain("http_route!=\"/health\"");
        query.Should().Contain("http_route!=\"/health/live\"");
        // The Prometheus scrape endpoint's own route (Program.cs:275, MapPrometheusScrapingEndpoint,
        // default path "/metrics" — confirmed live by the design spec's A36) must also be excluded,
        // or Prometheus's own periodic scrape of this API inflates "RPC request rate".
        query.Should().Contain("http_route!=\"/metrics\"");
    }

    public static IEnumerable<object[]> RpcHealthQueries()
    {
        yield return [PrometheusQueries.RpcRequestsPerSecond];
        yield return [PrometheusQueries.RpcErrorPercentage];
        yield return [PrometheusQueries.RpcP95Seconds];
    }

    /// <summary>
    /// THE NUMERATOR-AND-DENOMINATOR ASSERTION. <see cref="PrometheusQueries.RpcErrorPercentage"/>
    /// is a ratio of two `sum(rate(http_server_request_duration_seconds_count{...}))` clauses, and
    /// a plain `.Should().Contain(...)` on the whole string (as
    /// <see cref="EveryRpcHealthQuery_ExcludesHealthAndScrapeRoutes"/> above does, for the two
    /// single-clause queries) cannot tell "the filter is on both clauses" apart from "the filter
    /// is on only one clause" — a mutant that drops <c>RpcRouteFilter</c> from just the
    /// denominator would inflate that divisor with probe/scrape traffic, understate the error
    /// percentage, and still pass a substring-only check. Counted occurrences catch it: each of
    /// the three route exclusions must appear exactly twice — once per clause.
    /// </summary>
    [Theory]
    [InlineData("http_route!=\"/health\"")]
    [InlineData("http_route!=\"/health/live\"")]
    [InlineData("http_route!=\"/metrics\"")]
    public void RpcErrorPercentage_AppliesTheRouteFilter_ToBothClauses(string routeExclusion)
    {
        var occurrences = CountOccurrences(PrometheusQueries.RpcErrorPercentage, routeExclusion);

        occurrences.Should().Be(2,
            $"'{routeExclusion}' must be present in BOTH the numerator and the denominator of the ratio");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
