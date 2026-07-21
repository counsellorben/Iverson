import { render, screen, waitFor } from "@testing-library/react";
import { RouterProvider } from "react-router-dom";
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

  it("redirects / to /performance when authenticated", async () => {
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

    const { container } = render(<RouterProvider router={router} future={{ v7_startTransition: true }} />);

    // Verify the router navigated to /performance specifically (not /storage, /tenants, or /tenant-admin)
    await waitFor(() => {
      expect(window.location.pathname).toBe("/performance");
    });

    // The router should have navigated to /performance, which renders "Coming soon"
    expect(screen.getByText("Coming soon")).toBeInTheDocument();
  });
});
