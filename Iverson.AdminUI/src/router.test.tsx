import { render, screen, waitFor } from "@testing-library/react";
import { RouterProvider } from "react-router/dom";
import { createMemoryRouter, RouterProvider as MemoryRouterProvider } from "react-router";
import { describe, it, expect, vi, beforeEach } from "vitest";
import type { ApiResult } from "./api/client";
import type { ConsoleRolesResponse } from "./api/types";

const signinRedirect = vi.fn();
const useAuthMock = vi.fn();
const fetchRolesMock =
  vi.fn<(token: string, signal: AbortSignal) => Promise<ApiResult<ConsoleRolesResponse>>>();

vi.mock("react-oidc-context", () => ({
  useAuth: () => useAuthMock(),
}));

// Each test decides the caller's roles. The landing page's own reads never answer, so its cards stay
// loading and no state update escapes a test.
vi.mock("./api/console", () => ({
  fetchRoles: (token: string, signal: AbortSignal) => fetchRolesMock(token, signal),
  fetchHealth: () => new Promise(() => {}),
  fetchTenants: () => new Promise(() => {}),
  fetchSchema: () => new Promise(() => {}),
  fetchDataVolume: () => new Promise(() => {}),
  fetchMetrics: () => new Promise(() => {}),
  fetchQdrant: () => new Promise(() => {}),
}));

import { router, routes } from "./router";

const TOKEN = "router-access-token";

function signedIn(): void {
  useAuthMock.mockReturnValue({
    isLoading: false,
    isAuthenticated: true,
    user: { access_token: TOKEN, profile: { email: "test@example.com" } },
    signinRedirect,
    signoutRedirect: vi.fn(),
  });
}

function rolesAre(operator: boolean, tenantAdmin: boolean): void {
  fetchRolesMock.mockResolvedValue({ kind: "ok", status: 200, data: { operator, tenantAdmin } });
}

describe("router", () => {
  beforeEach(() => {
    signinRedirect.mockClear();
    useAuthMock.mockReset();
    fetchRolesMock.mockReset();
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

  it("renders the landing page at / for an operator, without redirecting away", async () => {
    signedIn();
    rolesAre(true, false);

    render(<RouterProvider router={router} future={{ v7_startTransition: true }} />);

    await waitFor(() => {
      expect(screen.getByRole("heading", { name: "Overview", level: 1 })).toBeInTheDocument();
    });
    expect(window.location.pathname).toBe("/");
    expect(screen.getByTestId("widget-tenants")).toBeInTheDocument();
    expect(fetchRolesMock).toHaveBeenCalledWith(TOKEN, expect.anything());
  });

  it("refuses the landing page to a signed-in user with neither console role", async () => {
    // CSR round 12 §2.2: the console is for admins only. The landing page used to render for every
    // signed-in user and degrade card by card.
    signedIn();
    rolesAre(false, false);

    render(<RouterProvider router={router} />);

    await waitFor(() => {
      expect(screen.getByTestId("require-role-denied")).toHaveTextContent(
        "Not authorized: the console is for operators and tenant admins."
      );
    });
    expect(screen.queryByRole("heading", { name: "Overview", level: 1 })).not.toBeInTheDocument();
  });
});

/**
 * Reachability, not linkage. `Sidebar.test.tsx` asserts that a caller without a role sees no nav LINK
 * to its page — which says nothing about what happens when the URL is typed, pasted, or bookmarked.
 * These mount the real route table at the privileged path directly.
 *
 * `createMemoryRouter` over the exported `routes` rather than the module's `router`, because
 * `createBrowserRouter` fixes its initial location at import time and cannot be re-pointed.
 */
describe("privileged routes", () => {
  function renderAt(path: string, operator: boolean, tenantAdmin: boolean) {
    signedIn();
    rolesAre(operator, tenantAdmin);
    const memoryRouter = createMemoryRouter(routes, { initialEntries: [path] });
    return render(<MemoryRouterProvider router={memoryRouter} />);
  }

  beforeEach(() => {
    signinRedirect.mockClear();
    useAuthMock.mockReset();
    fetchRolesMock.mockReset();
  });

  it("does not mount /tenants for a tenant admin", async () => {
    renderAt("/tenants", false, true);

    await waitFor(() => {
      expect(screen.getByTestId("require-role-denied")).toHaveTextContent(
        "Not authorized: this page requires the Operator role."
      );
    });
    expect(screen.queryByText("Coming soon")).not.toBeInTheDocument();
  });

  it("mounts /tenants for an operator", async () => {
    renderAt("/tenants", true, false);

    await waitFor(() => {
      expect(screen.getByText("Coming soon")).toBeInTheDocument();
    });
    expect(screen.queryByTestId("require-role-denied")).not.toBeInTheDocument();
  });

  it("does not mount /tenant-admin for an operator: the two roles answer different questions", async () => {
    renderAt("/tenant-admin", true, false);

    await waitFor(() => {
      expect(screen.getByTestId("require-role-denied")).toHaveTextContent(
        "Not authorized: this page requires the tenant admin role."
      );
    });
    expect(screen.queryByText("Coming soon")).not.toBeInTheDocument();
  });

  it("mounts /tenant-admin for a tenant admin", async () => {
    renderAt("/tenant-admin", false, true);

    await waitFor(() => {
      expect(screen.getByText("Coming soon")).toBeInTheDocument();
    });
  });

  it("has no /performance or /storage page any more", async () => {
    // An unmatched path ends in React Router's default 404 boundary, which logs; keep the run quiet.
    const consoleError = vi.spyOn(console, "error").mockImplementation(() => {});
    const consoleWarn = vi.spyOn(console, "warn").mockImplementation(() => {});
    try {
      for (const path of ["/performance", "/storage"]) {
        const { unmount } = renderAt(path, true, true);
        await waitFor(() => {
          expect(screen.getByText("404 Not Found")).toBeInTheDocument();
        });
        expect(screen.queryByText("Coming soon")).not.toBeInTheDocument();
        unmount();
      }
    } finally {
      consoleError.mockRestore();
      consoleWarn.mockRestore();
    }
  });
});
