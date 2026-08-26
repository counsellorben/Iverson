import { StrictMode } from "react";
import { render, screen, waitFor } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import type { ApiResult } from "../api/client";
import type { MetricsResponse } from "../api/types";
import { MetricsBand } from "./MetricsBand";
import { EMBEDDING_LATENCY_CAVEAT } from "./EmbeddingLatencyWidget";
import { NO_SAMPLE_VALUE } from "./MetricStat";
import { metricsAllNull, metricsOk, metricsUnavailable } from "./metricsFixture";

const fetchMetricsMock =
  vi.fn<(token: string, signal: AbortSignal) => Promise<ApiResult<MetricsResponse>>>();

vi.mock("../api/console", () => ({
  fetchMetrics: (token: string, signal: AbortSignal) => fetchMetricsMock(token, signal),
}));

const TOKEN = "test-access-token";

function card(): HTMLElement {
  return screen.getByTestId("widget-embedding-latency");
}

function cardState(): string | null {
  return card().getAttribute("data-state");
}

beforeEach(() => {
  fetchMetricsMock.mockReset();
});

describe("EmbeddingLatencyWidget", () => {
  it("puts the two-clients caveat ON THE CARD, beside the number it qualifies", async () => {
    // HTTP client metrics are labelled by `server.address`, never by the logical client name,
    // so a shared Ollama host makes this p95 cover enrichment as well as embeddings — and
    // enrichment is the slower of the two. The person who needs that caveat is reading the
    // number, not this file, so a code comment does not discharge it.
    fetchMetricsMock.mockResolvedValue(metricsOk());

    render(<MetricsBand accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    const caveat = screen.getByTestId("embedding-latency-caveat");
    expect(caveat).toBeInTheDocument();
    expect(caveat).toHaveTextContent(EMBEDDING_LATENCY_CAVEAT);
    // The two facts that make the caveat mean anything, pinned individually so a rewrite that
    // drops either one fails rather than merely reading differently.
    expect(EMBEDDING_LATENCY_CAVEAT).toContain("server address");
    expect(EMBEDDING_LATENCY_CAVEAT).toContain("covers both");
    expect(card()).toHaveTextContent("enrichment");
  });

  it("renders the p95 in the unit an operator reads", async () => {
    fetchMetricsMock.mockResolvedValue(metricsOk());

    render(<MetricsBand accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    expect(screen.getByTestId("embedding-p95-value")).toHaveTextContent("1.50 s");
  });

  it("shows a sub-second p95 in milliseconds", async () => {
    fetchMetricsMock.mockResolvedValue(metricsOk({ embeddingLatency: { p95Seconds: 0.128 } }));

    render(<MetricsBand accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    expect(screen.getByTestId("embedding-p95-value")).toHaveTextContent("128 ms");
  });

  it("NEVER renders an absent p95 as a zero-latency measurement", async () => {
    // This figure is also the console's ONLY signal about Ollama — /health has no tile for it
    // — so "0" here would read as "Ollama is answering instantly" when nothing was measured.
    fetchMetricsMock.mockResolvedValue(metricsAllNull());

    render(<MetricsBand accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    const value = screen.getByTestId("embedding-p95-value");
    expect(value).toHaveTextContent(NO_SAMPLE_VALUE);
    expect(value.textContent ?? "").not.toMatch(/\d/);
    expect(value).not.toHaveTextContent("ms");
    expect(screen.getByTestId("embedding-p95")).toHaveAttribute("data-metric-null", "true");
  });

  it("does not present an uninstalled Prometheus as an outage", async () => {
    fetchMetricsMock.mockResolvedValue(metricsUnavailable("notDeployed"));

    render(<MetricsBand accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("unavailable"));
    const notice = screen.getByTestId("widget-embedding-latency-notice");
    expect(notice).toHaveClass("MuiAlert-colorWarning");
    expect(notice).not.toHaveClass("MuiAlert-colorError");
    expect(notice).toHaveTextContent("not an outage");
  });

  it("reaches a rendered state under StrictMode", async () => {
    fetchMetricsMock.mockResolvedValue(metricsOk());

    render(<MetricsBand accessToken={TOKEN} />, { wrapper: StrictMode });

    await waitFor(() => expect(cardState()).toBe("ready"));
    expect(screen.getByTestId("embedding-p95-value")).toHaveTextContent("1.50 s");
    expect(screen.getByTestId("embedding-latency-caveat")).toBeInTheDocument();
    expect(card()).not.toHaveTextContent("Loading…");
  });
});
