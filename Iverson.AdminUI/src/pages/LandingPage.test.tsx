import { StrictMode } from "react";
import { render, screen, waitFor } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { LandingPage } from "./LandingPage";

const useAuthMock = vi.fn();
const fetchHealthMock = vi.fn();
const fetchTenantsMock = vi.fn();
const fetchSchemaMock = vi.fn();
const fetchDataVolumeMock = vi.fn();
const fetchMetricsMock = vi.fn();
const fetchQdrantMock = vi.fn();

vi.mock("react-oidc-context", () => ({
  useAuth: () => useAuthMock(),
}));

vi.mock("../api/console", () => ({
  fetchHealth: (t: string, s: AbortSignal) => fetchHealthMock(t, s),
  fetchTenants: (t: string, s: AbortSignal) => fetchTenantsMock(t, s),
  fetchSchema: (t: string, s: AbortSignal) => fetchSchemaMock(t, s),
  fetchDataVolume: (t: string, s: AbortSignal) => fetchDataVolumeMock(t, s),
  fetchMetrics: (t: string, s: AbortSignal) => fetchMetricsMock(t, s),
  fetchQdrant: (t: string, s: AbortSignal) => fetchQdrantMock(t, s),
}));

const TOKEN = "landing-access-token";

const ALL_MOCKS = [
  fetchHealthMock,
  fetchTenantsMock,
  fetchSchemaMock,
  fetchDataVolumeMock,
  fetchMetricsMock,
  fetchQdrantMock,
];

/** Every card's `data-testid`. Nine cards over six fetchers — Band B's four share one poll. */
const ALL_CARD_TEST_IDS = [
  "widget-health",
  "widget-tenants",
  "widget-schema",
  "widget-data-volume",
  "widget-fan-out-backlog",
  "widget-consumer-activity",
  "widget-transport-health",
  "widget-embedding-latency",
  "widget-qdrant",
];

const FORBIDDEN = { kind: "forbidden", status: 403, error: null };

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
    fetchMetricsMock.mockResolvedValue(FORBIDDEN);
    fetchQdrantMock.mockResolvedValue(FORBIDDEN);

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

    for (const testId of ALL_CARD_TEST_IDS) {
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
    fetchMetricsMock.mockResolvedValue({
      kind: "ok",
      status: 200,
      data: {
        fanOutBacklog: {
          reconciliationQueueDepth: 1,
          dlqUnreplayedCount: 0,
          documentRerenderQueueDepth: 2,
        },
        consumerActivity: { consumerRetriesPerSecond: 0, consumerDlqRoutedPerSecond: 0 },
        rpcHealth: { requestsPerSecond: 1, errorPercentage: 0, p95Seconds: 0.01 },
        embeddingLatency: { p95Seconds: 0.2 },
      },
    });
    fetchQdrantMock.mockResolvedValue({
      kind: "ok",
      status: 200,
      data: { collectionCount: 0, collections: [] },
    });

    render(<LandingPage />, { wrapper: StrictMode });

    await waitFor(() => {
      for (const testId of ALL_CARD_TEST_IDS) {
        if (testId === "widget-tenants") continue;
        expect(screen.getByTestId(testId)).toHaveAttribute("data-state", "ready");
      }
      expect(screen.getByTestId("widget-tenants")).toHaveAttribute("data-state", "forbidden");
    });
  });

  it("renders all nine cards, with Band B's four sharing a single metrics poll", async () => {
    signedIn(TOKEN);
    for (const mock of ALL_MOCKS) mock.mockResolvedValue(FORBIDDEN);
    fetchHealthMock.mockResolvedValue({
      kind: "ok",
      status: 200,
      data: { status: "healthy", checks: { postgres: true, starrocks: true, qdrant: true, kafka: true } },
    });

    render(<LandingPage />);

    await waitFor(() => {
      for (const testId of ALL_CARD_TEST_IDS) {
        expect(screen.getByTestId(testId)).toBeInTheDocument();
      }
      expect(screen.getByTestId("widget-transport-health")).toHaveAttribute(
        "data-state",
        "forbidden"
      );
    });
    // Four Band B cards, ONE request. A hook per card would be four requests and thirty-six
    // Prometheus queries every thirty seconds, per open tab.
    expect(fetchMetricsMock).toHaveBeenCalledTimes(1);
  });

  it("shows every Operator-gated card as 'not authorized', never as a failure", async () => {
    // This is the page a real human sees today: nobody satisfies the Operator policy, so
    // tenants, all four metrics cards and Qdrant all answer 403 (Design 4d). Six red "request
    // failed" cards would read as a broken deployment rather than a permissions gap.
    signedIn(TOKEN);
    for (const mock of ALL_MOCKS) mock.mockResolvedValue(FORBIDDEN);
    fetchHealthMock.mockResolvedValue({
      kind: "ok",
      status: 200,
      data: { status: "healthy", checks: { postgres: true, starrocks: true, qdrant: true, kafka: true } },
    });
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

    const operatorGated = [
      "widget-tenants",
      "widget-fan-out-backlog",
      "widget-consumer-activity",
      "widget-transport-health",
      "widget-embedding-latency",
      "widget-qdrant",
    ];

    render(<LandingPage />);

    await waitFor(() => {
      for (const testId of operatorGated) {
        expect(screen.getByTestId(testId)).toHaveAttribute("data-state", "forbidden");
      }
    });
    for (const testId of operatorGated) {
      const notice = screen.getByTestId(`${testId}-notice`);
      expect(notice).toHaveTextContent("Operator role");
      expect(notice).toHaveClass("MuiAlert-colorWarning");
      expect(notice).not.toHaveClass("MuiAlert-colorError");
    }
  });
});
