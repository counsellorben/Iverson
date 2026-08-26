import { StrictMode } from "react";
import { render, screen, waitFor } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import type { ApiResult } from "../api/client";
import type { DataVolumeResponse } from "../api/types";
import { DataVolumeWidget, describeDataVolumeReason } from "./DataVolumeWidget";

const fetchDataVolumeMock =
  vi.fn<(token: string, signal: AbortSignal) => Promise<ApiResult<DataVolumeResponse>>>();

vi.mock("../api/console", () => ({
  fetchDataVolume: (token: string, signal: AbortSignal) => fetchDataVolumeMock(token, signal),
}));

const TOKEN = "test-access-token";

function volume(over: Partial<DataVolumeResponse> = {}): ApiResult<DataVolumeResponse> {
  return {
    kind: "ok",
    status: 200,
    data: {
      types: [
        { typeName: "Contact", status: "counted", rowCount: 1234567 },
        { typeName: "Account", status: "counted", rowCount: 0 },
      ],
      deniedTypeCount: 0,
      unknownTypeCount: 0,
      ...over,
    },
  };
}

function cardState(): string | null {
  return screen.getByTestId("widget-data-volume").getAttribute("data-state");
}

beforeEach(() => {
  fetchDataVolumeMock.mockReset();
});

describe("describeDataVolumeReason", () => {
  it("renders a switched-off engagement store calmly and a not-ready one as transient", () => {
    expect(describeDataVolumeReason("disabled")).toContain("disabled in this deployment");
    expect(describeDataVolumeReason("notReady")).toContain("not ready yet");
    expect(describeDataVolumeReason("somethingElse")).toContain("somethingElse");
    expect(describeDataVolumeReason(null)).toBe("Currently unavailable.");
  });
});

describe("DataVolumeWidget", () => {
  it("labels itself tenant-scoped rather than a deployment total", async () => {
    fetchDataVolumeMock.mockResolvedValue(volume());

    render(<DataVolumeWidget accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    expect(screen.getByTestId("widget-data-volume")).toHaveTextContent(
      "Tenant-scoped row counts, not a deployment total."
    );
  });

  it("renders each counted type's rows, including a genuine zero", async () => {
    fetchDataVolumeMock.mockResolvedValue(volume());

    render(<DataVolumeWidget accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    expect(screen.getByTestId("data-volume-row-Contact")).toHaveAttribute(
      "data-row-count",
      "1234567"
    );
    expect(screen.getByTestId("data-volume-row-Contact")).toHaveTextContent("1,234,567");
    // A denied type has no entry at all, so this 0 is a real count and carries no hedge.
    expect(screen.getByTestId("data-volume-row-Account")).toHaveAttribute("data-row-count", "0");
  });

  it("counts denied and unknown types together into one 'not shown' line", async () => {
    fetchDataVolumeMock.mockResolvedValue(volume({ deniedTypeCount: 3, unknownTypeCount: 2 }));

    render(<DataVolumeWidget accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    const note = screen.getByTestId("data-volume-hidden-types");
    expect(note).toHaveAttribute("data-hidden-count", "5");
    expect(note).toHaveTextContent("5 types not shown (not visible to you).");
  });

  it("renders no 'not shown' line when the list is complete", async () => {
    fetchDataVolumeMock.mockResolvedValue(volume());

    render(<DataVolumeWidget accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    expect(screen.queryByTestId("data-volume-hidden-types")).not.toBeInTheDocument();
  });

  it("renders a 503 'disabled' body as a configuration statement, not a fault", async () => {
    fetchDataVolumeMock.mockResolvedValue({
      kind: "problem",
      status: 503,
      reason: "disabled",
      error: "Engagement store disabled",
      body: {},
    });

    render(<DataVolumeWidget accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("unavailable"));
    expect(screen.getByTestId("widget-data-volume-notice")).toHaveTextContent(
      "The engagement store is disabled in this deployment"
    );
  });

  it("waits for the session rather than spinning when there is no token", () => {
    render(<DataVolumeWidget accessToken={undefined} />);

    expect(cardState()).toBe("awaitingToken");
    expect(screen.getByRole("button", { name: "Refresh Data volume" })).toBeDisabled();
    expect(fetchDataVolumeMock).not.toHaveBeenCalled();
  });

  it("fetches once on mount and never polls, because each call is a COUNT(*) per type", async () => {
    fetchDataVolumeMock.mockResolvedValue(volume());

    render(<DataVolumeWidget accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    expect(fetchDataVolumeMock).toHaveBeenCalledTimes(1);
  });

  it("reaches a rendered state under StrictMode", async () => {
    fetchDataVolumeMock.mockResolvedValue(volume({ deniedTypeCount: 4 }));

    render(<DataVolumeWidget accessToken={TOKEN} />, { wrapper: StrictMode });

    await waitFor(() => expect(cardState()).toBe("ready"));
    expect(screen.getByTestId("data-volume-hidden-types")).toHaveAttribute("data-hidden-count", "4");
  });
});
