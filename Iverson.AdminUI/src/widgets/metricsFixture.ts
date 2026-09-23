import type { ApiResult } from "../api/client";
import type { MetricsResponse } from "../api/types";

/**
 * Test-only fixtures for `/admin/console/metrics`.
 *
 * Shared by the four Band B widget test files, which all drive the same one response through
 * `MetricsBand`. The default deliberately mixes a **genuine zero** (`dlqUnreplayedCount: 0` —
 * nothing is sitting in the DLQ) with ordinary non-zero figures, so a test that renders the
 * default is already exercising the distinction every one of these widgets has to keep: a zero
 * is a measurement, a `null` is the absence of one.
 */

export function metricsData(over: Partial<MetricsResponse> = {}): MetricsResponse {
  return {
    fanOutBacklog: {
      reconciliationQueueDepth: 12,
      dlqUnreplayedCount: 0,
      documentRerenderQueueDepth: 3456,
    },
    consumerActivity: {
      consumerRetriesPerSecond: 0.25,
      consumerDlqRoutedPerSecond: 0,
    },
    rpcHealth: {
      requestsPerSecond: 42.5,
      errorPercentage: 1.46,
      p95Seconds: 0.043,
    },
    embeddingLatency: { p95Seconds: 1.5 },
    ...over,
  };
}

export function metricsOk(over: Partial<MetricsResponse> = {}): ApiResult<MetricsResponse> {
  return { kind: "ok", status: 200, data: metricsData(over) };
}

/** Every value absent — what a deployment with no worker scrape job and a cold API looks like. */
export function metricsAllNull(): ApiResult<MetricsResponse> {
  return metricsOk({
    fanOutBacklog: {
      reconciliationQueueDepth: null,
      dlqUnreplayedCount: null,
      documentRerenderQueueDepth: null,
    },
    consumerActivity: { consumerRetriesPerSecond: null, consumerDlqRoutedPerSecond: null },
    rpcHealth: { requestsPerSecond: null, errorPercentage: null, p95Seconds: null },
    embeddingLatency: { p95Seconds: null },
  });
}

/** The 503 body the endpoint sends, as the fetch layer surfaces it. */
export function metricsUnavailable(reason: string): ApiResult<MetricsResponse> {
  return {
    kind: "problem",
    status: 503,
    reason,
    error: "Prometheus could not be queried",
    body: { reason, error: "Prometheus could not be queried" },
  };
}

/** What every human gets from this endpoint today: an Operator-gated 403 with no body. */
export function metricsForbidden(): ApiResult<MetricsResponse> {
  return { kind: "forbidden", status: 403, error: null };
}
