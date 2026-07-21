import { render, screen } from "@testing-library/react";
import { RouterProvider } from "react-router-dom";
import { describe, it, expect, vi, beforeEach } from "vitest";

const signinRedirect = vi.fn();

vi.mock("react-oidc-context", () => ({
  useAuth: () => ({
    isLoading: false,
    isAuthenticated: false,
    signinRedirect,
  }),
}));

import { router } from "./router";

describe("router", () => {
  beforeEach(() => {
    signinRedirect.mockClear();
  });

  it("redirects an unauthenticated visitor at the root route instead of rendering the app", () => {
    render(<RouterProvider router={router} future={{ v7_startTransition: true }} />);

    expect(screen.queryByText("Admin UI")).not.toBeInTheDocument();
    expect(signinRedirect).toHaveBeenCalledTimes(1);
  });
});
