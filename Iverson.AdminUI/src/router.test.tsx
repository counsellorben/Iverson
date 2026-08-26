import { render, screen, waitFor } from "@testing-library/react";
import { RouterProvider } from "react-router/dom";
import { describe, it, expect, vi, beforeEach } from "vitest";

const signinRedirect = vi.fn();
const useAuthMock = vi.fn();

vi.mock("react-oidc-context", () => ({
  useAuth: () => useAuthMock(),
}));

import { router } from "./router";

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
