import { StrictMode } from "react";
import { render, screen, waitFor } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import type { ApiResult } from "../api/client";
import type { MetricsResponse } from "../api/types";
import { MetricsBand } from "./MetricsBand";
import {
  TRANSPORT_HEALTH_SUBTITLE,
  TRANSPORT_HEALTH_TITLE,
} from "./TransportHealthWidget";
import { NO_SAMPLE_VALUE } from "./MetricStat";
import { metricsAllNull, metricsForbidden, metricsOk } from "./metricsFixture";

const fetchMetricsMock =
  vi.fn<(token: string, signal: AbortSignal) => Promise<ApiResult<MetricsResponse>>>();

vi.mock("../api/console", () => ({
  fetchMetrics: (token: string, signal: AbortSignal) => fetchMetricsMock(token, signal),
}));

const TOKEN = "test-access-token";

function card(): HTMLElement {
  return screen.getByTestId("widget-transport-health");
}

function cardState(): string | null {
  return card().getAttribute("data-state");
}

beforeEach(() => {
  fetchMetricsMock.mockReset();
});

describe("TransportHealthWidget", () => {
  it("never calls these figures RPC — anywhere on the card", async () => {
    // `AddAspNetCoreInstrumentation` observes gRPC-over-HTTP/2 as HTTP requests, and gRPC
    // carries its status in a trailer inside a 200. So a deployment where every gRPC call is
    // failing with PermissionDenied shows 0% here. A card labelled "RPC errors" would state
    // the one thing this number cannot know, and an operator would read it as all-clear.
    //
    // The assertion is deliberately blunt — no "RPC" in the rendered card at all — because
    // the failure mode is a well-meaning rename, and a blunt assertion is what a rename hits.
    fetchMetricsMock.mockResolvedValue(metricsOk());

    render(<MetricsBand accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    // "RPC" not preceded by a `g` — the subtitle is allowed, and required, to say "gRPC"
    // while explaining why gRPC failures are invisible here.
    const bareRpc = /(?<![gG])RPC/;
    expect(card().textContent ?? "").not.toMatch(bareRpc);
    expect(TRANSPORT_HEALTH_TITLE).not.toMatch(bareRpc);
    expect(TRANSPORT_HEALTH_SUBTITLE).not.toMatch(bareRpc);
    expect(screen.getByRole("heading", { name: TRANSPORT_HEALTH_TITLE })).toBeInTheDocument();
  });

  it("says HTTP on the title, the subtitle and the error figure's own label", async () => {
    // The number gets quoted out of context; one of the three travels with it.
    fetchMetricsMock.mockResolvedValue(metricsOk());

    render(<MetricsBand accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    expect(TRANSPORT_HEALTH_TITLE).toContain("HTTP");
    expect(card()).toHaveTextContent(TRANSPORT_HEALTH_SUBTITLE);
    expect(TRANSPORT_HEALTH_SUBTITLE).toContain("HTTP 200");
    expect(screen.getByTestId("transport-error-percentage")).toHaveTextContent("HTTP error");
  });

  it("renders request rate, error percentage and p95 in readable units", async () => {
    fetchMetricsMock.mockResolvedValue(metricsOk());

    render(<MetricsBand accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    expect(screen.getByTestId("transport-request-rate-value")).toHaveTextContent("42.50 /s");
    expect(screen.getByTestId("transport-error-percentage-value")).toHaveTextContent("1.5%");
    // 0.043 seconds off the wire, read by a human as 43 ms.
    expect(screen.getByTestId("transport-p95-value")).toHaveTextContent("43 ms");
  });

  it("NEVER renders an absent error percentage as 0%", async () => {
    // "0% of requests are failing" and "nothing has been measured" are opposite readings.
    fetchMetricsMock.mockResolvedValue(metricsAllNull());

    render(<MetricsBand accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    for (const testId of ["transport-request-rate", "transport-error-percentage", "transport-p95"]) {
      const value = screen.getByTestId(`${testId}-value`);
      expect(value).toHaveTextContent(NO_SAMPLE_VALUE);
      expect(value.textContent ?? "").not.toMatch(/\d/);
      expect(value).not.toHaveTextContent("%");
      expect(screen.getByTestId(testId)).toHaveAttribute("data-metric-null", "true");
    }
  });

  it("renders a measured zero error percentage as a zero", async () => {
    fetchMetricsMock.mockResolvedValue(
      metricsOk({ rpcHealth: { requestsPerSecond: 10, errorPercentage: 0, p95Seconds: 0.001 } })
    );

    render(<MetricsBand accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    expect(screen.getByTestId("transport-error-percentage-value")).toHaveTextContent("0.0%");
    expect(screen.getByTestId("transport-error-percentage")).toHaveAttribute(
      "data-metric-null",
      "false"
    );
  });

  it("renders the Operator 403 as an authorization answer at warning severity", async () => {
    fetchMetricsMock.mockResolvedValue(metricsForbidden());

    render(<MetricsBand accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("forbidden"));
    const notice = screen.getByTestId("widget-transport-health-notice");
    expect(notice).toHaveTextContent("require the Operator role");
    expect(notice).toHaveClass("MuiAlert-colorWarning");
    expect(notice).not.toHaveClass("MuiAlert-colorError");
  });

  it("reaches a rendered state under StrictMode", async () => {
    fetchMetricsMock.mockResolvedValue(metricsOk());

    render(<MetricsBand accessToken={TOKEN} />, { wrapper: StrictMode });

    await waitFor(() => expect(cardState()).toBe("ready"));
    expect(screen.getByTestId("transport-p95-value")).toHaveTextContent("43 ms");
    expect(card()).not.toHaveTextContent("Loading…");
  });
});
