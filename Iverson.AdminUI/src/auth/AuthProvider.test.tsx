import { render, screen } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";

const signinRedirect = vi.fn();
const useAuthMock = vi.fn();

vi.mock("react-oidc-context", () => ({
  useAuth: () => useAuthMock(),
}));

import { AuthGate } from "./AuthProvider";

describe("AuthGate", () => {
  beforeEach(() => {
    signinRedirect.mockClear();
    useAuthMock.mockReset();
  });

  it("redirects an unauthenticated visitor into the login flow instead of rendering children", () => {
    useAuthMock.mockReturnValue({
      isLoading: false,
      isAuthenticated: false,
      signinRedirect,
    });

    render(
      <AuthGate>
        <div>Protected content</div>
      </AuthGate>
    );

    expect(screen.queryByText("Protected content")).not.toBeInTheDocument();
    expect(signinRedirect).toHaveBeenCalledTimes(1);
  });

  it("renders children once authenticated", () => {
    useAuthMock.mockReturnValue({
      isLoading: false,
      isAuthenticated: true,
      signinRedirect,
    });

    render(
      <AuthGate>
        <div>Protected content</div>
      </AuthGate>
    );

    expect(screen.getByText("Protected content")).toBeInTheDocument();
    expect(signinRedirect).not.toHaveBeenCalled();
  });

  it("does not redirect while the auth state is still loading", () => {
    useAuthMock.mockReturnValue({
      isLoading: true,
      isAuthenticated: false,
      signinRedirect,
    });

    render(
      <AuthGate>
        <div>Protected content</div>
      </AuthGate>
    );

    expect(screen.queryByText("Protected content")).not.toBeInTheDocument();
    expect(signinRedirect).not.toHaveBeenCalled();
  });
});
