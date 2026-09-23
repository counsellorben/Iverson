import { StrictMode } from "react";
import { render, screen, waitFor } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import type { ApiResult } from "../api/client";
import type { MetricsResponse } from "../api/types";
import { MetricsBand } from "./MetricsBand";
import { CONSUMER_ACTIVITY_SUBTITLE } from "./ConsumerActivityWidget";
import { describeMetricsReason } from "./MetricsWidgetCard";
import { NO_SAMPLE_VALUE } from "./format";
import {
  metricsAllNull,
  metricsOk,
  metricsUnavailable,
} from "./metricsFixture";

const fetchMetricsMock =
  vi.fn<(token: string, signal: AbortSignal) => Promise<ApiResult<MetricsResponse>>>();

vi.mock("../api/console", () => ({
  fetchMetrics: (token: string, signal: AbortSignal) => fetchMetricsMock(token, signal),
}));

const TOKEN = "test-access-token";

function card(): HTMLElement {
  return screen.getByTestId("widget-consumer-activity");
}

function cardState(): string | null {
  return card().getAttribute("data-state");
}

beforeEach(() => {
  fetchMetricsMock.mockReset();
});

describe("describeMetricsReason", () => {
  it("says notDeployed is a supported configuration and unreachable is not", () => {
    // The two 503 reasons are different facts. `notDeployed` — values-laptop.yaml sets
    // prometheus.enabled: false — needs nobody to do anything; `unreachable` does.
    const notDeployed = describeMetricsReason("notDeployed");
    const unreachable = describeMetricsReason("unreachable");

    expect(notDeployed).toContain("not installed");
    expect(notDeployed).toContain("not an outage");
    expect(unreachable).toContain("not answering");
    expect(notDeployed).not.toBe(unreachable);
    expect(notDeployed).not.toContain("not answering");
    expect(describeMetricsReason("somethingElse")).toContain("somethingElse");
    expect(describeMetricsReason(null)).toBe("Currently unavailable.");
  });
});

describe("ConsumerActivityWidget", () => {
  it("renders both rates with their unit on the figure, including a genuine zero", async () => {
    fetchMetricsMock.mockResolvedValue(metricsOk());

    render(<MetricsBand accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    expect(screen.getByTestId("consumer-retries-rate-value")).toHaveTextContent("0.25 /s");
    // Nothing is being routed to the DLQ: a measured zero, and it says so as a rate.
    expect(screen.getByTestId("consumer-dlq-routed-rate-value")).toHaveTextContent("0.00 /s");
  });

  it("does not render a live DLQ trickle as a stopped one", async () => {
    // 0.004/s is about fourteen messages an hour landing in the DLQ. Rounded to two places
    // it is "0.00 /s" — the exact string the test above pins as meaning "nothing is being
    // routed to the DLQ" — with `data-metric-null` correctly false, so nothing flags it.
    fetchMetricsMock.mockResolvedValue(
      metricsOk({
        consumerActivity: { consumerRetriesPerSecond: 0.004, consumerDlqRoutedPerSecond: 0 },
      })
    );

    render(<MetricsBand accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    const trickle = screen.getByTestId("consumer-retries-rate-value");
    const stopped = screen.getByTestId("consumer-dlq-routed-rate-value");
    expect(trickle).toHaveTextContent("<0.01 /s");
    expect(trickle).not.toHaveTextContent("0.00 /s");
    expect(stopped).toHaveTextContent("0.00 /s");
    expect(trickle.textContent).not.toBe(stopped.textContent);
  });

  it("NEVER renders an absent rate as a zero rate", async () => {
    // `consumer_retries_total` and `consumer_dlq_routed_total` are worker-only. "0.00 /s"
    // where the truth is "no worker scrape job" reads as a healthy consumer.
    fetchMetricsMock.mockResolvedValue(metricsAllNull());

    render(<MetricsBand accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    for (const testId of ["consumer-retries-rate", "consumer-dlq-routed-rate"]) {
      const value = screen.getByTestId(`${testId}-value`);
      expect(value).toHaveTextContent(NO_SAMPLE_VALUE);
      expect(value.textContent ?? "").not.toMatch(/\d/);
      expect(value).not.toHaveTextContent("/s");
      expect(screen.getByTestId(testId)).toHaveAttribute("data-metric-null", "true");
    }
  });

  it("does not make an absent Prometheus LOOK like an outage", async () => {
    // `prometheus.enabled: false` is a supported profile. The severity is the promise here:
    // the sentence can say "supported configuration" all it likes while a red alert box
    // tells the operator something is broken, and colour wins at a glance.
    fetchMetricsMock.mockResolvedValue(metricsUnavailable("notDeployed"));

    render(<MetricsBand accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("unavailable"));
    const notice = screen.getByTestId("widget-consumer-activity-notice");
    expect(notice).toHaveClass("MuiAlert-colorWarning");
    expect(notice).not.toHaveClass("MuiAlert-colorError");
    expect(notice).toHaveTextContent("Prometheus is not installed in this deployment");
    expect(notice).toHaveTextContent("not an outage");
  });

  it("still distinguishes an unreachable Prometheus, which IS worth acting on", async () => {
    fetchMetricsMock.mockResolvedValue(metricsUnavailable("unreachable"));

    render(<MetricsBand accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("unavailable"));
    const notice = screen.getByTestId("widget-consumer-activity-notice");
    expect(notice).toHaveTextContent("installed but is not answering");
    expect(notice).not.toHaveTextContent("not installed in this deployment");
  });

  it("renders a transport failure as a real error, so warning keeps meaning something", async () => {
    fetchMetricsMock.mockResolvedValue({ kind: "failed", status: null, message: "network down" });

    render(<MetricsBand accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("error"));
    const notice = screen.getByTestId("widget-consumer-activity-notice");
    expect(notice).toHaveClass("MuiAlert-colorError");
    expect(notice).toHaveTextContent("network down");
  });

  it("names the worker as the source and says absent is not zero", async () => {
    fetchMetricsMock.mockResolvedValue(metricsOk());

    render(<MetricsBand accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    expect(card()).toHaveTextContent(CONSUMER_ACTIVITY_SUBTITLE);
    expect(CONSUMER_ACTIVITY_SUBTITLE).toContain("worker");
    expect(CONSUMER_ACTIVITY_SUBTITLE).toContain("not zero");
  });

  it("reaches a rendered state under StrictMode", async () => {
    fetchMetricsMock.mockResolvedValue(metricsOk());

    render(<MetricsBand accessToken={TOKEN} />, { wrapper: StrictMode });

    await waitFor(() => expect(cardState()).toBe("ready"));
    expect(screen.getByTestId("consumer-retries-rate-value")).toHaveTextContent("0.25 /s");
    expect(card()).not.toHaveTextContent("Loading…");
  });
});
