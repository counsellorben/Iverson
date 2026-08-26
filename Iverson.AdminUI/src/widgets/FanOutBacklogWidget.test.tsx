import { StrictMode } from "react";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import type { ApiResult } from "../api/client";
import type { MetricsResponse } from "../api/types";
import { MetricsBand } from "./MetricsBand";
import { FAN_OUT_BACKLOG_SUBTITLE } from "./FanOutBacklogWidget";
import { NO_SAMPLE_VALUE } from "./format";
import { metricsAllNull, metricsForbidden, metricsOk } from "./metricsFixture";

const fetchMetricsMock =
  vi.fn<(token: string, signal: AbortSignal) => Promise<ApiResult<MetricsResponse>>>();

vi.mock("../api/console", () => ({
  fetchMetrics: (token: string, signal: AbortSignal) => fetchMetricsMock(token, signal),
}));

const TOKEN = "test-access-token";

function card(): HTMLElement {
  return screen.getByTestId("widget-fan-out-backlog");
}

function cardState(): string | null {
  return card().getAttribute("data-state");
}

beforeEach(() => {
  fetchMetricsMock.mockReset();
});

describe("FanOutBacklogWidget", () => {
  it("renders all three queue depths, including a genuine zero", async () => {
    fetchMetricsMock.mockResolvedValue(metricsOk());

    render(<MetricsBand accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    expect(screen.getByTestId("fan-out-reconciliation-queue-depth-value")).toHaveTextContent("12");
    expect(screen.getByTestId("fan-out-document-rerender-queue-depth-value")).toHaveTextContent(
      "3,456"
    );
    // A measured empty DLQ IS a zero, and must render as one.
    expect(screen.getByTestId("fan-out-dlq-unreplayed-value")).toHaveTextContent("0");
    expect(screen.getByTestId("fan-out-dlq-unreplayed")).toHaveAttribute(
      "data-metric-null",
      "false"
    );
  });

  it("NEVER renders an absent metric as a zero — not one digit of it", async () => {
    // The five worker-only instruments come back null wherever Prometheus has no worker
    // scrape job. "The reconciliation queue is empty" and "nobody is measuring the
    // reconciliation queue" are the two most different readings a single card can carry, so
    // this asserts the VISIBLE figure, not merely the flag standing in for it: a `?? 0` in
    // the widget puts a "0" in this element and fails here.
    fetchMetricsMock.mockResolvedValue(metricsAllNull());

    render(<MetricsBand accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    for (const testId of [
      "fan-out-reconciliation-queue-depth",
      "fan-out-dlq-unreplayed",
      "fan-out-document-rerender-queue-depth",
    ]) {
      const value = screen.getByTestId(`${testId}-value`);
      expect(value).toHaveTextContent(NO_SAMPLE_VALUE);
      expect(value.textContent ?? "").not.toMatch(/\d/);
      expect(screen.getByTestId(testId)).toHaveAttribute("data-metric-null", "true");
      expect(screen.getByTestId(testId)).toHaveTextContent("not a zero");
    }
  });

  it("renders an absent figure and a zero figure differently in the same card", async () => {
    fetchMetricsMock.mockResolvedValue(
      metricsOk({
        fanOutBacklog: {
          reconciliationQueueDepth: null,
          dlqUnreplayedCount: 0,
          documentRerenderQueueDepth: null,
        },
      })
    );

    render(<MetricsBand accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    const absent = screen.getByTestId("fan-out-reconciliation-queue-depth-value");
    const zero = screen.getByTestId("fan-out-dlq-unreplayed-value");
    expect(absent.textContent).not.toBe(zero.textContent);
    expect(zero).toHaveTextContent("0");
    expect(absent.textContent ?? "").not.toMatch(/\d/);
  });

  it("names the worker as the source, so 'No data' points at the scrape config", async () => {
    fetchMetricsMock.mockResolvedValue(metricsAllNull());

    render(<MetricsBand accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    expect(card()).toHaveTextContent(FAN_OUT_BACKLOG_SUBTITLE);
    expect(FAN_OUT_BACKLOG_SUBTITLE).toContain("worker");
    expect(FAN_OUT_BACKLOG_SUBTITLE).toContain("not zero");
  });

  it("renders the 403 every human gets today as an authorization answer, not a fault", async () => {
    fetchMetricsMock.mockResolvedValue(metricsForbidden());

    render(<MetricsBand accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("forbidden"));
    const notice = screen.getByTestId("widget-fan-out-backlog-notice");
    expect(notice).toHaveTextContent("require the Operator role");
    // Severity is the promise: an authorization answer in error red reads as a break.
    expect(notice).toHaveClass("MuiAlert-colorWarning");
    expect(notice).not.toHaveClass("MuiAlert-colorError");
  });

  it("keeps the last good depths on screen and marks them stale when a poll fails", async () => {
    fetchMetricsMock
      .mockResolvedValueOnce(metricsOk())
      .mockResolvedValue({ kind: "failed", status: null, message: "network down" });

    render(<MetricsBand accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    fireEvent.click(screen.getByRole("button", { name: "Refresh Fan-out backlog" }));

    await waitFor(() =>
      expect(screen.getByTestId("widget-fan-out-backlog-as-of")).toHaveAttribute(
        "data-stale",
        "true"
      )
    );
    expect(screen.getByTestId("fan-out-reconciliation-queue-depth-value")).toHaveTextContent("12");
  });

  it("waits for the session rather than spinning when there is no token", () => {
    render(<MetricsBand accessToken={undefined} />);

    expect(cardState()).toBe("awaitingToken");
    expect(screen.getByRole("button", { name: "Refresh Fan-out backlog" })).toBeDisabled();
    expect(fetchMetricsMock).not.toHaveBeenCalled();
  });

  it("reaches a rendered state under StrictMode", async () => {
    fetchMetricsMock.mockResolvedValue(metricsOk());

    render(<MetricsBand accessToken={TOKEN} />, { wrapper: StrictMode });

    // Not merely "it rendered": a hook that never leaves `loading` under StrictMode's
    // mount/unmount/remount leaves this card on the spinner forever, which is exactly the
    // Critical a previous task shipped invisibly to `tsc`, to the build and to the suite.
    await waitFor(() => expect(cardState()).toBe("ready"));
    expect(screen.getByTestId("fan-out-document-rerender-queue-depth-value")).toHaveTextContent(
      "3,456"
    );
    expect(card()).not.toHaveTextContent("Loading…");
  });

  it("polls the metrics endpoint ONCE for all four Band B cards", async () => {
    fetchMetricsMock.mockResolvedValue(metricsOk());

    render(<MetricsBand accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    // One body carries all four widgets' figures. Four hooks would be four requests and
    // thirty-six Prometheus queries every thirty seconds, per open tab.
    expect(fetchMetricsMock).toHaveBeenCalledTimes(1);
    for (const testId of [
      "widget-fan-out-backlog",
      "widget-consumer-activity",
      "widget-transport-health",
      "widget-embedding-latency",
    ]) {
      expect(screen.getByTestId(testId)).toHaveAttribute("data-state", "ready");
    }
  });
});
