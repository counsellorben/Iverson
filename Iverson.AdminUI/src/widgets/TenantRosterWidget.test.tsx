import { StrictMode } from "react";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import type { ApiResult } from "../api/client";
import type { TenantsResponse } from "../api/types";
import { TenantRosterWidget } from "./TenantRosterWidget";

const fetchTenantsMock =
  vi.fn<(token: string, signal: AbortSignal) => Promise<ApiResult<TenantsResponse>>>();

vi.mock("../api/console", () => ({
  fetchTenants: (token: string, signal: AbortSignal) => fetchTenantsMock(token, signal),
}));

const TOKEN = "test-access-token";

const ROSTER: ApiResult<TenantsResponse> = {
  kind: "ok",
  status: 200,
  data: {
    count: 2,
    tenants: [
      {
        id: "11111111-1111-1111-1111-111111111111",
        displayName: "Acme",
        status: "Active",
        createdAt: "2026-01-02T03:04:05.678Z",
      },
      {
        id: "22222222-2222-2222-2222-222222222222",
        displayName: "Globex",
        status: "Suspended",
        createdAt: "2026-02-03T04:05:06Z",
      },
    ],
  },
};

function cardState(): string | null {
  return screen.getByTestId("widget-tenants").getAttribute("data-state");
}

beforeEach(() => {
  fetchTenantsMock.mockReset();
});

describe("TenantRosterWidget", () => {
  it("renders the roster", async () => {
    fetchTenantsMock.mockResolvedValue(ROSTER);

    render(<TenantRosterWidget accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    expect(screen.getByTestId("tenant-count")).toHaveAttribute("data-count", "2");
    expect(screen.getByText("Acme")).toBeInTheDocument();
    expect(screen.getByText("Globex")).toBeInTheDocument();
    // The instant is UTC off the wire and is LABELLED as such, because the card's own
    // "Updated {asOf} local time" line is the viewer's clock. Two unlabelled clocks on one
    // card is how an operator several hours off UTC reads a normal timestamp as a fault.
    expect(
      screen.getByTestId("tenant-row-11111111-1111-1111-1111-111111111111")
    ).toHaveTextContent("2026-01-02 03:04 UTC");
    expect(screen.getByTestId("widget-tenants-as-of")).toHaveTextContent(/local time/);
  });

  it("keeps the last good roster on screen and marks it stale when a refresh fails", async () => {
    fetchTenantsMock
      .mockResolvedValueOnce(ROSTER)
      .mockResolvedValue({ kind: "failed", status: null, message: "network down" });

    render(<TenantRosterWidget accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    fireEvent.click(screen.getByRole("button", { name: "Refresh Tenants" }));

    await waitFor(() =>
      expect(screen.getByTestId("widget-tenants-as-of")).toHaveAttribute("data-stale", "true")
    );
    expect(screen.getByText("Acme")).toBeInTheDocument();
  });

  it("renders a 403 as an explicit not-authorized state, not as an error", async () => {
    // This is what EVERY human gets today: the endpoint is Operator-gated and nobody
    // satisfies the Operator policy in the deployment (Design 4d). The 403 also carries no
    // body, so there is nothing to read out of it but the status.
    fetchTenantsMock.mockResolvedValue({ kind: "forbidden", status: 403, error: null });

    render(<TenantRosterWidget accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("forbidden"));
    const notice = screen.getByTestId("widget-tenants-notice");
    expect(notice).toHaveTextContent("Not authorized: listing tenants requires the Operator role.");
    expect(screen.queryByText(/Could not load/)).not.toBeInTheDocument();
    expect(screen.queryByRole("table", { name: "Tenants" })).not.toBeInTheDocument();

    // Contract fact 3 is a promise about how this LOOKS, so it is asserted visually. The same
    // sentence rendered in error red is still a generic error card to anyone glancing at it.
    expect(notice).toHaveClass("MuiAlert-colorWarning");
    expect(notice).not.toHaveClass("MuiAlert-colorError");
  });

  it("distinguishes a transport failure from a 403", async () => {
    fetchTenantsMock.mockResolvedValue({ kind: "failed", status: null, message: "network down" });

    render(<TenantRosterWidget accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("error"));
    expect(screen.getByTestId("widget-tenants-notice")).toHaveTextContent("network down");
  });

  it("waits for the session rather than spinning when there is no token", () => {
    render(<TenantRosterWidget accessToken={undefined} />);

    expect(cardState()).toBe("awaitingToken");
    expect(screen.getByRole("button", { name: "Refresh Tenants" })).toBeDisabled();
    expect(fetchTenantsMock).not.toHaveBeenCalled();
  });

  it("fetches once on mount and does not poll", async () => {
    fetchTenantsMock.mockResolvedValue(ROSTER);

    render(<TenantRosterWidget accessToken={TOKEN} />);

    await waitFor(() => expect(cardState()).toBe("ready"));
    expect(fetchTenantsMock).toHaveBeenCalledTimes(1);
  });

  it("reaches a rendered state under StrictMode", async () => {
    fetchTenantsMock.mockResolvedValue({ kind: "forbidden", status: 403, error: null });

    render(<TenantRosterWidget accessToken={TOKEN} />, { wrapper: StrictMode });

    await waitFor(() => expect(cardState()).toBe("forbidden"));
  });
});
