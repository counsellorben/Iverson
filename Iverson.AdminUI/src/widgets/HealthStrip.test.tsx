import { StrictMode } from "react";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import type { ApiResult } from "../api/client";
import type { HealthChecks, HealthResponse } from "../api/types";
import {
  CHECK_APPEARANCE,
  HEALTH_POLL_INTERVAL_MS,
  HealthStrip,
  describeCadence,
  describeCheck,
} from "./HealthStrip";

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

  it("derives the cadence it advertises from the constant it actually polls on", async () => {
    fetchHealthMock.mockResolvedValue(healthy());

    render(<HealthStrip accessToken={TOKEN} />);

    await waitFor(() => expect(screen.getByTestId("health-tile-postgres")).toBeInTheDocument());
    // Not the literal "Polled every 60 seconds." — that sentence would keep passing after the
    // constant changed, which is exactly how a widget comes to advertise a cadence it no
    // longer uses.
    expect(screen.getByTestId("widget-health")).toHaveTextContent(
      describeCadence(HEALTH_POLL_INTERVAL_MS)
    );
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

  it("does not make a deliberately disabled store LOOK like a failure", async () => {
    // The state name and the word "Disabled" are not the promise. The promise is that an
    // operator glancing at the strip does not see a red chip on a store they switched off
    // themselves — and colour beats text at a glance, so colour is what gets asserted.
    fetchHealthMock.mockResolvedValue(healthy({ starrocks: "disabled", qdrant: false }));

    render(<HealthStrip accessToken={TOKEN} />);

    await waitFor(() => expect(tileState("starrocks")).toBe("disabled"));
    const disabledTile = screen.getByTestId("health-tile-starrocks");
    expect(disabledTile).toHaveClass("MuiChip-colorDefault");
    expect(disabledTile).toHaveClass("MuiChip-outlined");
    expect(disabledTile).not.toHaveClass("MuiChip-colorError");
    expect(disabledTile).not.toHaveClass("MuiChip-filled");

    // …and a store that really IS down still looks like one, so the contrast carries meaning.
    const downTile = screen.getByTestId("health-tile-qdrant");
    expect(downTile).toHaveClass("MuiChip-colorError");
    expect(downTile).toHaveClass("MuiChip-filled");
  });

  it("pins the appearance of every check state", () => {
    expect(CHECK_APPEARANCE.disabled).toEqual({ color: "default", variant: "outlined" });
    expect(CHECK_APPEARANCE.down).toEqual({ color: "error", variant: "filled" });
    expect(CHECK_APPEARANCE.up).toEqual({ color: "success", variant: "filled" });
    expect(CHECK_APPEARANCE.unknown).toEqual({ color: "warning", variant: "outlined" });
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

  it("renders a transport failure as an error with nothing to show", async () => {
    fetchHealthMock.mockResolvedValue({ kind: "failed", status: null, message: "network down" });

    render(<HealthStrip accessToken={TOKEN} />);

    await waitFor(() =>
      expect(screen.getByTestId("widget-health")).toHaveAttribute("data-state", "error")
    );
    expect(screen.getByTestId("widget-health-notice")).toHaveTextContent("network down");
    expect(screen.queryByTestId("health-tile-postgres")).not.toBeInTheDocument();
  });

  it("keeps the last good tiles on screen and marks them stale when a later fetch fails", async () => {
    fetchHealthMock
      .mockResolvedValueOnce(healthy({ starrocks: "disabled" }))
      .mockResolvedValue({ kind: "failed", status: null, message: "network down" });

    render(<HealthStrip accessToken={TOKEN} />);

    await waitFor(() => expect(tileState("starrocks")).toBe("disabled"));
    fireEvent.click(screen.getByRole("button", { name: "Refresh Store health" }));

    await waitFor(() =>
      expect(screen.getByTestId("widget-health-as-of")).toHaveAttribute("data-stale", "true")
    );
    // The tiles are RETAINED, not replaced by a spinner or blanked.
    expect(tileState("starrocks")).toBe("disabled");
    expect(screen.queryByText("Loading…")).not.toBeInTheDocument();
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
