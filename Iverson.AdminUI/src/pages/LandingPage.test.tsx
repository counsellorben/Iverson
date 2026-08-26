import { StrictMode } from "react";
import { render, screen, waitFor } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { LandingPage } from "./LandingPage";

const useAuthMock = vi.fn();
const fetchHealthMock = vi.fn();
const fetchTenantsMock = vi.fn();
const fetchSchemaMock = vi.fn();
const fetchDataVolumeMock = vi.fn();

vi.mock("react-oidc-context", () => ({
  useAuth: () => useAuthMock(),
}));

vi.mock("../api/console", () => ({
  fetchHealth: (t: string, s: AbortSignal) => fetchHealthMock(t, s),
  fetchTenants: (t: string, s: AbortSignal) => fetchTenantsMock(t, s),
  fetchSchema: (t: string, s: AbortSignal) => fetchSchemaMock(t, s),
  fetchDataVolume: (t: string, s: AbortSignal) => fetchDataVolumeMock(t, s),
}));

const TOKEN = "landing-access-token";

const ALL_MOCKS = [fetchHealthMock, fetchTenantsMock, fetchSchemaMock, fetchDataVolumeMock];

beforeEach(() => {
  useAuthMock.mockReset();
  for (const mock of ALL_MOCKS) mock.mockReset();
});

function signedIn(accessToken: string | undefined): void {
  useAuthMock.mockReturnValue({
    isLoading: false,
    isAuthenticated: true,
    user: accessToken === undefined ? { profile: {} } : { access_token: accessToken, profile: {} },
  });
}

describe("LandingPage", () => {
  it("renders all four Band A widgets", async () => {
    signedIn(TOKEN);
    fetchHealthMock.mockResolvedValue({
      kind: "ok",
      status: 200,
      data: {
        status: "healthy",
        checks: { postgres: true, starrocks: "disabled", qdrant: true, kafka: true },
      },
    });
    fetchTenantsMock.mockResolvedValue({ kind: "forbidden", status: 403, error: null });
    fetchSchemaMock.mockResolvedValue({
      kind: "ok",
      status: 200,
      data: { typeCount: 0, types: [], withheldTypeCount: 2 },
    });
    fetchDataVolumeMock.mockResolvedValue({
      kind: "ok",
      status: 200,
      data: { types: [], deniedTypeCount: 1, unknownTypeCount: 0 },
    });

    render(<LandingPage />);

    expect(screen.getByRole("heading", { name: "Overview", level: 1 })).toBeInTheDocument();
    expect(screen.getByTestId("widget-health")).toBeInTheDocument();
    expect(screen.getByTestId("widget-tenants")).toBeInTheDocument();
    expect(screen.getByTestId("widget-schema")).toBeInTheDocument();
    expect(screen.getByTestId("widget-data-volume")).toBeInTheDocument();

    // Settle every widget's first fetch before the test ends, so no state update escapes act.
    await waitFor(() => {
      expect(screen.getByTestId("widget-tenants")).toHaveAttribute("data-state", "forbidden");
      expect(screen.getByTestId("health-tile-starrocks")).toHaveAttribute(
        "data-check-state",
        "disabled"
      );
      expect(screen.getByTestId("schema-hidden-types")).toHaveAttribute("data-hidden-count", "2");
      expect(screen.getByTestId("data-volume-denied-types")).toHaveAttribute(
        "data-hidden-count",
        "1"
      );
    });
  });

  it("threads the session's access token into every widget's fetch", async () => {
    signedIn(TOKEN);
    for (const mock of ALL_MOCKS) mock.mockResolvedValue({ kind: "forbidden", status: 403, error: null });

    render(<LandingPage />);

    await waitFor(() => expect(fetchHealthMock).toHaveBeenCalled());
    for (const mock of ALL_MOCKS) {
      expect(mock).toHaveBeenCalledWith(TOKEN, expect.anything());
    }
  });

  it("puts every widget in the waiting-for-session state before a token exists", () => {
    signedIn(undefined);

    render(<LandingPage />);

    for (const testId of ["widget-health", "widget-tenants", "widget-schema", "widget-data-volume"]) {
      expect(screen.getByTestId(testId)).toHaveAttribute("data-state", "awaitingToken");
    }
    for (const mock of ALL_MOCKS) expect(mock).not.toHaveBeenCalled();
  });

  it("renders under StrictMode without any widget stranded on the spinner", async () => {
    signedIn(TOKEN);
    fetchHealthMock.mockResolvedValue({
      kind: "ok",
      status: 200,
      data: { status: "healthy", checks: { postgres: true, starrocks: true, qdrant: true, kafka: true } },
    });
    fetchTenantsMock.mockResolvedValue({ kind: "forbidden", status: 403, error: null });
    fetchSchemaMock.mockResolvedValue({
      kind: "ok",
      status: 200,
      data: { typeCount: 0, types: [], withheldTypeCount: 0 },
    });
    fetchDataVolumeMock.mockResolvedValue({
      kind: "ok",
      status: 200,
      data: { types: [], deniedTypeCount: 0, unknownTypeCount: 0 },
    });

    render(<LandingPage />, { wrapper: StrictMode });

    await waitFor(() => {
      for (const testId of ["widget-health", "widget-schema", "widget-data-volume"]) {
        expect(screen.getByTestId(testId)).toHaveAttribute("data-state", "ready");
      }
      expect(screen.getByTestId("widget-tenants")).toHaveAttribute("data-state", "forbidden");
    });
  });
});
