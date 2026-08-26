import { StrictMode } from "react";
import { render, screen, waitFor } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import type { ApiResult } from "../api/client";
import type { HealthChecks, HealthResponse } from "../api/types";
import { HEALTH_POLL_INTERVAL_MS, HealthStrip, describeCheck } from "./HealthStrip";

const fetchHealthMock = vi.fn<(token: string, signal: AbortSignal) => Promise<ApiResult<HealthResponse>>>();

vi.mock("../api/console", () => ({
  fetchHealth: (token: string, signal: AbortSignal) => fetchHealthMock(token, signal),
}));

const TOKEN = "test-access-token";

function healthy(overrides: Partial<HealthChecks> = {}): ApiResult<HealthResponse> {
  return {
    kind: "ok",
    status: 200,
    data: {
      status: "healthy",
      checks: { postgres: true, starrocks: true, qdrant: true, kafka: true, ...overrides },
    },
  };
}

function tileState(store: string): string | null {
  return screen.getByTestId(`health-tile-${store}`).getAttribute("data-check-state");
}

beforeEach(() => {
  fetchHealthMock.mockReset();
});

describe("describeCheck", () => {
  it("keeps StarRocks's three states apart", () => {
    expect(describeCheck(true)).toBe("up");
    expect(describeCheck(false)).toBe("down");
    // The literal string, NOT a falsy boolean. Rendering this as "down" would put a red tile
    // on a deployment whose engagement store was switched off on purpose.
    expect(describeCheck("disabled")).toBe("disabled");
    expect(describeCheck(undefined)).toBe("unknown");
  });
});

describe("HealthStrip", () => {
  it("polls at 60 seconds, the cadence for a write-bearing endpoint", () => {
    expect(HEALTH_POLL_INTERVAL_MS).toBe(60_000);
  });

  it("renders one tile per store, and none for Ollama", async () => {
    fetchHealthMock.mockResolvedValue(healthy());

    render(<HealthStrip accessToken={TOKEN} />);

    await waitFor(() => expect(screen.getByTestId("health-tile-postgres")).toBeInTheDocument());
    expect(screen.getByTestId("health-tile-starrocks")).toBeInTheDocument();
    expect(screen.getByTestId("health-tile-qdrant")).toBeInTheDocument();
    expect(screen.getByTestId("health-tile-kafka")).toBeInTheDocument();
    // /health does not check Ollama; its state comes from the Band B latency widget.
    expect(screen.queryByTestId("health-tile-ollama")).not.toBeInTheDocument();
  });

  it("renders a disabled StarRocks as Disabled, distinct from Down", async () => {
    fetchHealthMock.mockResolvedValue(healthy({ starrocks: "disabled" }));

    render(<HealthStrip accessToken={TOKEN} />);

    await waitFor(() => expect(tileState("starrocks")).toBe("disabled"));
    expect(screen.getByTestId("health-tile-starrocks")).toHaveTextContent("StarRocks: Disabled");
    expect(tileState("postgres")).toBe("up");
  });

  it("renders a failed StarRocks check as Down", async () => {
    fetchHealthMock.mockResolvedValue(healthy({ starrocks: false }));

    render(<HealthStrip accessToken={TOKEN} />);

    await waitFor(() => expect(tileState("starrocks")).toBe("down"));
    expect(screen.getByTestId("health-tile-starrocks")).toHaveTextContent("StarRocks: Down");
  });

  it("renders the degraded body /health returns with a 503", async () => {
    // fetchHealth declares 503 body-bearing, so a degraded deployment arrives as kind: "ok".
    fetchHealthMock.mockResolvedValue({
      kind: "ok",
      status: 503,
      data: {
        status: "degraded",
        checks: { postgres: true, starrocks: true, qdrant: false, kafka: true },
      },
    });

    render(<HealthStrip accessToken={TOKEN} />);

    await waitFor(() =>
      expect(screen.getByTestId("health-status")).toHaveAttribute("data-status", "degraded")
    );
    expect(screen.getByTestId("widget-health")).toHaveAttribute("data-state", "ready");
    expect(tileState("qdrant")).toBe("down");
  });

  it("waits for the session rather than spinning when there is no token", () => {
    render(<HealthStrip accessToken={undefined} />);

    expect(screen.getByTestId("widget-health")).toHaveAttribute("data-state", "awaitingToken");
    expect(screen.getByRole("button", { name: "Refresh Store health" })).toBeDisabled();
    expect(fetchHealthMock).not.toHaveBeenCalled();
  });

  it("reaches a rendered state under StrictMode", async () => {
    fetchHealthMock.mockResolvedValue(healthy({ starrocks: "disabled" }));

    render(<HealthStrip accessToken={TOKEN} />, { wrapper: StrictMode });

    await waitFor(() =>
      expect(screen.getByTestId("widget-health")).toHaveAttribute("data-state", "ready")
    );
    expect(tileState("starrocks")).toBe("disabled");
  });
});
