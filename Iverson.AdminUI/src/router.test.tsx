import { render, screen, waitFor } from "@testing-library/react";
import { RouterProvider } from "react-router/dom";
import { createMemoryRouter, RouterProvider as MemoryRouterProvider } from "react-router";
import { describe, it, expect, vi, beforeEach } from "vitest";

const signinRedirect = vi.fn();
const useAuthMock = vi.fn();

vi.mock("react-oidc-context", () => ({
  useAuth: () => useAuthMock(),
}));

import { router, routes } from "./router";

describe("router", () => {
  beforeEach(() => {
    signinRedirect.mockClear();
    useAuthMock.mockReset();
    // jsdom's location persists across tests in a file; pin it so the "no redirect" assertion
    // below is about the router's behaviour and not about what an earlier test left behind.
    window.history.replaceState({}, "", "/");
  });

  it("redirects an unauthenticated visitor at the root route instead of rendering the app", () => {
    useAuthMock.mockReturnValue({
      isLoading: false,
      isAuthenticated: false,
      signinRedirect,
    });

    render(<RouterProvider router={router} future={{ v7_startTransition: true }} />);

    expect(signinRedirect).toHaveBeenCalledTimes(1);
  });

  it("renders the landing page at / when authenticated, without redirecting away", async () => {
    // The index route used to be <Navigate to="/performance" replace />. Task 10 replaced it
    // with the landing page, so the assertion is inverted: the router must now STAY at "/"
    // and render the overview rather than navigating to any of the four sub-pages.
    useAuthMock.mockReturnValue({
      isLoading: false,
      isAuthenticated: true,
      user: {
        profile: {
          email: "test@example.com",
          groups: [],
        },
      },
      signoutRedirect: vi.fn(),
    });

    render(<RouterProvider router={router} future={{ v7_startTransition: true }} />);

    // The landing page renders, and it renders at "/" — no redirect to /performance,
    // /storage, /tenants or /tenant-admin.
    await waitFor(() => {
      expect(screen.getByRole("heading", { name: "Overview", level: 1 })).toBeInTheDocument();
    });
    expect(window.location.pathname).toBe("/");
    expect(screen.queryByText("Coming soon")).not.toBeInTheDocument();

    // Its four Band A widgets are mounted. No access token is present on the mocked session,
    // so each one reports that it is waiting for the session rather than spinning forever.
    for (const testId of ["widget-health", "widget-tenants", "widget-schema", "widget-data-volume"]) {
      expect(screen.getByTestId(testId)).toHaveAttribute("data-state", "awaitingToken");
    }
  });
});

/**
 * Reachability, not linkage. `Sidebar.test.tsx` asserts that a non-member sees no nav LINK to
 * a privileged page — which says nothing about what happens when the URL is typed, pasted, or
 * bookmarked. These mount the real route table at the privileged path directly.
 *
 * `createMemoryRouter` over the exported `routes` rather than the module's `router`, because
 * `createBrowserRouter` fixes its initial location at import time and cannot be re-pointed.
 */
describe("privileged routes", () => {
  function renderAt(path: string, groups: unknown) {
    useAuthMock.mockReturnValue({
      isLoading: false,
      isAuthenticated: true,
      user: { profile: { email: "test@example.com", groups } },
      signinRedirect,
      signoutRedirect: vi.fn(),
    });

    const memoryRouter = createMemoryRouter(routes, { initialEntries: [path] });
    render(<MemoryRouterProvider router={memoryRouter} />);
  }

  beforeEach(() => {
    signinRedirect.mockClear();
    useAuthMock.mockReset();
  });

  it("does not mount /tenants for an authenticated non-operator", async () => {
    renderAt("/tenants", []);

    await waitFor(() => {
      expect(screen.getByTestId("require-group-denied")).toBeInTheDocument();
    });
    expect(screen.queryByText("Coming soon")).not.toBeInTheDocument();
  });

  it("mounts /tenants for an operator", async () => {
    renderAt("/tenants", ["operators"]);

    await waitFor(() => {
      expect(screen.getByText("Coming soon")).toBeInTheDocument();
    });
    expect(screen.queryByTestId("require-group-denied")).not.toBeInTheDocument();
  });

  it("does not mount /tenants for a member of a group that merely contains 'operators'", async () => {
    // Substring matching in the guard would let `non-operators` through. Whole-name matching
    // must not.
    renderAt("/tenants", ["non-operators"]);

    await waitFor(() => {
      expect(screen.getByTestId("require-group-denied")).toBeInTheDocument();
    });
    expect(screen.queryByText("Coming soon")).not.toBeInTheDocument();
  });

  it("does not mount /tenant-admin for an authenticated non-tenant-admin", async () => {
    // An operator is NOT automatically a tenant admin: the two guards ask different questions.
    renderAt("/tenant-admin", ["operators"]);

    await waitFor(() => {
      expect(screen.getByTestId("require-group-denied")).toBeInTheDocument();
    });
    expect(screen.queryByText("Coming soon")).not.toBeInTheDocument();
  });

  it("mounts /tenant-admin for a tenant admin", async () => {
    renderAt("/tenant-admin", ["tenant-admins"]);

    await waitFor(() => {
      expect(screen.getByText("Coming soon")).toBeInTheDocument();
    });
  });
});
